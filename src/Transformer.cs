// The decoder forward pass (Llama / Mistral / Qwen2 / Qwen3), run one streamed stage at a time. C# 5 only.
// Numerics follow llama.cpp's llm_build_llama / llm_build_qwen2 / llm_build_qwen3 (MIT): RMSNorm, RoPE (normal or
// NEOX), grouped-query attention with a KV cache, SwiGLU feed-forward. CPU: scalar + Parallel.For + unsafe pointers.
using System;
using System.Threading;
using System.Threading.Tasks;

namespace Windlass
{
    public sealed unsafe class Transformer : IDisposable
    {
        public readonly ModelConfig C;
        public readonly LayerStream Stream;
        readonly GgufTensor _embd;
        readonly float[] _ropeFreqs;            // rope_freqs.weight (Llama 3.1+ long-context factors), or null
        readonly Sequence _own;                 // the KV cache Forward uses: per layer [position][KvDim], fp16 like llama.cpp's default
        readonly float[][] _rope;               // per position: cos/sin pairs, computed once (the same for every layer)

        // Worker threads for the matrix and attention loops (0 = every logical core). Set from the machine playbook.
        public static int Threads;
        static ParallelOptions Par { get { ParallelOptions o = new ParallelOptions(); o.MaxDegreeOfParallelism = Threads > 0 ? Threads : -1; return o; } }

        public readonly int MaxContext;
        public int Length { get { return _own.Length; } } // positions held in the KV cache

        public Transformer(GgufModelFile g, ModelConfig c, LayerStream stream, int maxContext)
        {
            C = c;
            Stream = stream;
            MaxContext = maxContext;
            _embd = g.TensorsByName["token_embd.weight"];
            GgufTensor rf;
            if (g.TensorsByName.TryGetValue("rope_freqs.weight", out rf)) _ropeFreqs = ReadVector(g, rf);
            _own = new Sequence(c.Layers);
            _rope = new float[maxContext][];
        }

        static float[] ReadVector(GgufModelFile g, GgufTensor t)
        {
            byte[] raw = new byte[t.SizeBytes];
            using (System.IO.FileStream fs = new System.IO.FileStream(g.Paths[t.FileIndex], System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.Read))
            {
                fs.Position = t.FileOffset;
                int at = 0;
                while (at < raw.Length) { int n = fs.Read(raw, at, raw.Length - at); if (n <= 0) throw new System.IO.EndOfStreamException(); at += n; }
            }
            float[] v = new float[t.ElementCount];
            fixed (byte* p = raw) fixed (float* d = v) Quants.DecodeRow(t.Type, p, v.Length, d);
            return v;
        }

        // Drops cached positions from n onward (the cache keeps a shared prompt prefix between requests).
        public void Truncate(int n) { _own.Truncate(n); }

        // An empty KV cache of its own, for ForwardBatch (--batch): one per request that generates at the same time.
        public Sequence NewSequence() { return new Sequence(C.Layers); }

        void Reserve(Sequence sq, int positions)
        {
            if (positions <= sq.Capacity) return;
            int cap = Math.Max(positions, Math.Min(MaxContext, Math.Max(256, sq.Capacity * 2)));
            for (int l = 0; l < C.Layers; l++)
            {
                ushort[] nk = new ushort[(long)cap * C.KvDim], nv = new ushort[(long)cap * C.KvDim];
                if (sq.K[l] != null) { Array.Copy(sq.K[l], nk, (long)sq.Length * C.KvDim); Array.Copy(sq.V[l], nv, (long)sq.Length * C.KvDim); }
                sq.K[l] = nk; sq.V[l] = nv;
            }
            sq.Capacity = cap;
        }

        // Runs tokens at positions pos0.. through every stage. Returns logits for the last token, or for every token
        // when allLogits is set ([T][Vocab] flattened). onStage(done, total) reports progress; cancel stops between stages.
        public float[] Forward(int[] tokens, int pos0, bool allLogits, CancellationToken cancel, Action<int, int> onStage)
        {
            return Forward(_own, tokens, pos0, allLogits, cancel, onStage);
        }

        // The same on one sequence's own KV cache (see NewSequence).
        public float[] Forward(Sequence sq, int[] tokens, int pos0, bool allLogits, CancellationToken cancel, Action<int, int> onStage)
        {
            int T = tokens.Length, E = C.Embd;
            if (T == 0) throw new ArgumentException("No tokens to run.");
            if (pos0 > sq.Length) throw new ArgumentException("pos0 is past the cached positions.");
            if (pos0 + T > MaxContext) throw new ArgumentException("The prompt and reply need " + (pos0 + T) + " positions; the context is " + MaxContext + ".");
            sq.Length = pos0;
            Reserve(sq, pos0 + T);

            float[] x = new float[(long)T * E];
            fixed (float* xp = x) for (int t = 0; t < T; t++) Stream.EmbedRow(_embd, tokens[t], xp + (long)t * E);

            int total = C.Layers + 1;
            for (int l = 0; l < C.Layers; l++)
            {
                cancel.ThrowIfCancellationRequested();
                Stage s = Stream.Acquire(l);
                Layer(s, l, x, T, pos0, sq, null);
                if (onStage != null) onStage(l + 1, total);
            }
            cancel.ThrowIfCancellationRequested();
            float[] logits = Head(x, T, allLogits ? 0 : T - 1);
            if (onStage != null) onStage(total, total);
            sq.Length = pos0 + T;
            return logits;
        }

        // Final norm and output head for rows first..T-1 of x.
        float[] Head(float[] x, int T, int first)
        {
            int E = C.Embd, n = T - first;
            Stage head = Stream.Acquire(Stream.HeadStage);
            float[] last = new float[(long)n * E];
            Array.Copy(x, (long)first * E, last, 0, (long)n * E);
            float[] xn = new float[last.Length];
            RmsNormRows(last, n, E, Vector(head.Get("output_norm.weight")), C.RmsEps, xn);
            WeightView output = head.Get("output.weight");
            float[] logits = new float[(long)n * output.Rows];
            MatMul(output, xn, n, logits);
            return logits;
        }

        // One decode step for several sequences at once: token i is the next token of seqs[i], at position
        // seqs[i].Length. The N tokens are stacked into ONE pass, so every layer's weights are read and decoded
        // once for all of them; attention runs per sequence on that sequence's own KV cache. Every other operation
        // works row by row, and the matrices use the single-token kernels for every row (see EachToken), so row i of
        // the result is bit-identical to Forward(seqs[i], new int[] { tokens[i] }, ...). Returns [N][Vocab] logits.
        public float[] ForwardBatch(Sequence[] seqs, int[] tokens, CancellationToken cancel, Action<int, int> onStage)
        {
            int N = tokens.Length, E = C.Embd;
            if (N == 0 || seqs.Length != N) throw new ArgumentException("ForwardBatch needs one token for each sequence.");
            BatchRows br = new BatchRows();
            br.Seq = seqs; br.Pos = new int[N];
            for (int i = 0; i < N; i++)
            {
                Sequence sq = seqs[i];
                for (int j = 0; j < i; j++) if (seqs[j] == sq) throw new ArgumentException("A sequence can take one token per batch step.");
                int pos = sq.Length;
                if (pos + 1 > MaxContext) throw new ArgumentException("The prompt and reply need " + (pos + 1) + " positions; the context is " + MaxContext + ".");
                br.Pos[i] = pos;
                if (pos + 1 > br.MaxN) br.MaxN = pos + 1;
                Reserve(sq, pos + 1);
            }
            float[] x = new float[(long)N * E];
            fixed (float* xp = x) for (int t = 0; t < N; t++) Stream.EmbedRow(_embd, tokens[t], xp + (long)t * E);

            int total = C.Layers + 1;
            EachToken = true;
            try
            {
                for (int l = 0; l < C.Layers; l++)
                {
                    cancel.ThrowIfCancellationRequested();
                    Stage s = Stream.Acquire(l);
                    Layer(s, l, x, N, 0, null, br);
                    if (onStage != null) onStage(l + 1, total);
                }
                cancel.ThrowIfCancellationRequested();
                float[] logits = Head(x, N, 0);
                if (onStage != null) onStage(total, total);
                for (int i = 0; i < N; i++) seqs[i].Length = br.Pos[i] + 1;
                return logits;
            }
            finally { EachToken = false; }
        }

        // Rows of a batch step: row t belongs to Seq[t] at position Pos[t]; MaxN is the longest history plus one.
        sealed class BatchRows { public Sequence[] Seq; public int[] Pos; public int MaxN; }

        // br == null: T tokens of the sequence sq from position pos0 (a prompt or a few guesses). Otherwise a batch step:
        // T rows, each the next token of its own sequence (sq and pos0 are unused).
        void Layer(Stage s, int l, float[] x, int T, int pos0, Sequence sq, BatchRows br)
        {
            int E = C.Embd, QD = C.QDim, KD = C.KvDim, HD = C.HeadDim;
            float[] xn = new float[x.Length];
            RmsNormRows(x, T, E, Vector(s.Get("attn_norm.weight")), C.RmsEps, xn);

            float[] q = new float[(long)T * QD], k = new float[(long)T * KD], v = new float[(long)T * KD];
            MatMul(s.Get("attn_q.weight"), xn, T, q);
            MatMul(s.Get("attn_k.weight"), xn, T, k);
            MatMul(s.Get("attn_v.weight"), xn, T, v);
            AddBias(s.Find("attn_q.bias"), q, T);
            AddBias(s.Find("attn_k.bias"), k, T);
            AddBias(s.Find("attn_v.bias"), v, T);
            if (C.QkNorm)
            {
                RmsNormRows(q, T * C.Heads, HD, Vector(s.Get("attn_q_norm.weight")), C.RmsEps, q);
                RmsNormRows(k, T * C.KvHeads, HD, Vector(s.Get("attn_k_norm.weight")), C.RmsEps, k);
            }
            for (int t = 0; t < T; t++)
            {
                float[] cs = RopeTable(br == null ? pos0 + t : br.Pos[t]);
                Rope(q, (long)t * QD, C.Heads, cs);
                Rope(k, (long)t * KD, C.KvHeads, cs);
            }
            if (br == null)
            {
                ToHalf(k, sq.K[l], (long)pos0 * KD, (long)T * KD);
                ToHalf(v, sq.V[l], (long)pos0 * KD, (long)T * KD);
            }
            else
                for (int t = 0; t < T; t++)
                {
                    ToHalf(k, (long)t * KD, br.Seq[t].K[l], (long)br.Pos[t] * KD, KD);
                    ToHalf(v, (long)t * KD, br.Seq[t].V[l], (long)br.Pos[t] * KD, KD);
                }

            float[] att = new float[(long)T * QD];
            Attention(sq, br, l, q, T, pos0, att);
            float[] o = new float[(long)T * E];
            MatMul(s.Get("attn_output.weight"), att, T, o);
            for (long i = 0; i < x.Length; i++) x[i] += o[i];

            RmsNormRows(x, T, E, Vector(s.Get("ffn_norm.weight")), C.RmsEps, xn);
            WeightView gate = s.Get("ffn_gate.weight");
            int F = gate.Rows;
            float[] g = new float[(long)T * F], u = new float[(long)T * F];
            MatMul(gate, xn, T, g);
            MatMul(s.Get("ffn_up.weight"), xn, T, u);
            for (long i = 0; i < g.Length; i++) { float a = g[i]; g[i] = a / (1f + (float)Math.Exp(-a)) * u[i]; }
            MatMul(s.Get("ffn_down.weight"), g, T, o);
            for (long i = 0; i < x.Length; i++) x[i] += o[i];
        }

        static void ToHalf(float[] src, ushort[] dst, long at, long n) { ToHalf(src, 0, dst, at, n); }

        static void ToHalf(float[] src, long from, ushort[] dst, long at, long n)
        {
            fixed (float* s = src) fixed (ushort* d = dst)
                for (long i = 0; i < n; i++) d[at + i] = Half.FromFloat(s[from + i]);
        }

        // Row t of a batch attends to its own sequence's positions 0..Pos[t]; otherwise to sq's, through pos0 + t.
        void Attention(Sequence sq, BatchRows br, int l, float[] q, int T, int pos0, float[] outp)
        {
            int H = C.Heads, HD = C.HeadDim, QD = C.QDim, KD = C.KvDim, group = C.Heads / C.KvHeads;
            float scale = 1f / (float)Math.Sqrt(HD);
            float[] table = Half.Lookup;
            int scratch = br == null ? pos0 + T : br.MaxN;
            Parallel.For(0, T * H, Par, () => new float[scratch], delegate(int job, ParallelLoopState st, float[] scores)
            {
                int t = job / H, h = job % H, kvh = h / group;
                int n = br == null ? pos0 + t + 1 : br.Pos[t] + 1;
                ushort[] K = br == null ? sq.K[l] : br.Seq[t].K[l], V = br == null ? sq.V[l] : br.Seq[t].V[l];
                fixed (float* qp0 = q) fixed (ushort* Kp = K) fixed (ushort* Vp = V) fixed (float* op0 = outp) fixed (float* sp = scores) fixed (float* tab = table)
                {
                    float* qp = qp0 + (long)t * QD + h * HD;
                    float max = float.NegativeInfinity;
                    for (int j = 0; j < n; j++)
                    {
                        ushort* kp = Kp + (long)j * KD + kvh * HD;
                        float s0 = 0, s1 = 0, s2 = 0, s3 = 0;
                        int i = 0;
                        for (; i + 4 <= HD; i += 4)
                        {
                            s0 += qp[i] * tab[kp[i]]; s1 += qp[i + 1] * tab[kp[i + 1]];
                            s2 += qp[i + 2] * tab[kp[i + 2]]; s3 += qp[i + 3] * tab[kp[i + 3]];
                        }
                        for (; i < HD; i++) s0 += qp[i] * tab[kp[i]];
                        float d = ((s0 + s1) + (s2 + s3)) * scale;
                        sp[j] = d;
                        if (d > max) max = d;
                    }
                    double sum = 0;
                    for (int j = 0; j < n; j++) { float e = (float)Math.Exp(sp[j] - max); sp[j] = e; sum += e; }
                    float inv = (float)(1.0 / sum);
                    float* op = op0 + (long)t * QD + h * HD;
                    for (int d = 0; d < HD; d++) op[d] = 0;
                    for (int j = 0; j < n; j++)
                    {
                        float p = sp[j] * inv;
                        ushort* vp = Vp + (long)j * KD + kvh * HD;
                        for (int d = 0; d < HD; d++) op[d] += p * tab[vp[d]];
                    }
                }
                return scores;
            }, delegate(float[] scores) { });
        }

        // cos/sin per rotated pair at one position: cs[2i] = cos, cs[2i+1] = sin (ggml rope with freq_factors).
        // Computed once per position and reused by every layer and every later request.
        float[] RopeTable(int pos)
        {
            float[] cached = _rope[pos];
            if (cached != null) return cached;
            int n = C.RopeDims;
            float[] cs = new float[n];
            double thetaScale = Math.Pow(C.RopeBase, -2.0 / n);
            double theta = pos * (double)C.RopeFreqScale;
            for (int i = 0; i < n / 2; i++)
            {
                double t = _ropeFreqs != null ? theta / _ropeFreqs[i] : theta;
                cs[2 * i] = (float)Math.Cos(t);
                cs[2 * i + 1] = (float)Math.Sin(t);
                theta *= thetaScale;
            }
            _rope[pos] = cs;
            return cs;
        }

        void Rope(float[] x, long offset, int heads, float[] cs)
        {
            int HD = C.HeadDim, n = C.RopeDims, half = n / 2;
            for (int h = 0; h < heads; h++)
            {
                long o = offset + (long)h * HD;
                for (int i = 0; i < half; i++)
                {
                    float c = cs[2 * i], s = cs[2 * i + 1];
                    long a = C.RopeNeox ? o + i : o + 2 * i, b = C.RopeNeox ? o + i + half : o + 2 * i + 1;
                    float x0 = x[a], x1 = x[b];
                    x[a] = x0 * c - x1 * s;
                    x[b] = x0 * s + x1 * c;
                }
            }
        }

        static float[] Vector(WeightView w)
        {
            float[] v = new float[(long)w.Rows * w.Cols];
            fixed (float* d = v) Quants.DecodeRow(w.Type, w.Data, v.Length, d);
            return v;
        }

        static void AddBias(WeightView w, float[] y, int T)
        {
            if (w == null) return;
            float[] b = Vector(w);
            for (int t = 0; t < T; t++)
            {
                long o = (long)t * b.Length;
                for (int i = 0; i < b.Length; i++) y[o + i] += b[i];
            }
        }

        // y[r] = x[r] / sqrt(mean(x[r]^2) + eps) * w, for each of n rows of width dim (in place allowed).
        public static void RmsNormRows(float[] x, int n, int dim, float[] w, float eps, float[] y)
        {
            for (int r = 0; r < n; r++)
            {
                long o = (long)r * dim;
                double sum = 0;
                for (int i = 0; i < dim; i++) sum += (double)x[o + i] * x[o + i];
                float scale = 1f / (float)Math.Sqrt((float)(sum / dim) + eps);
                for (int i = 0; i < dim; i++) y[o + i] = x[o + i] * scale * w[i];
            }
        }

        public static float Dot(float* a, float* b, int n)
        {
            float s0 = 0, s1 = 0, s2 = 0, s3 = 0;
            int i = 0;
            for (; i + 4 <= n; i += 4)
            {
                s0 += a[i] * b[i]; s1 += a[i + 1] * b[i + 1];
                s2 += a[i + 2] * b[i + 2]; s3 += a[i + 3] * b[i + 3];
            }
            for (; i < n; i++) s0 += a[i] * b[i];
            return (s0 + s1) + (s2 + s3);
        }

        const int RowTile = 32;

        // y[t][r] = dot(W[r], x[t]) for T input rows. One token (decoding) uses the fused kernels; a batch (the prompt)
        // decodes each tile of weight rows once and reuses it for every t.
        public static void MatMul(WeightView w, float[] x, int T, float[] y)
        {
            int rows = w.Rows, cols = w.Cols;
            if ((long)cols * T != x.Length || (long)rows * T > y.Length) throw new ArgumentException("MatMul shape mismatch for " + w.Name + ".");
            if (EachToken && T > 1 && Quants.CanDot(w.Type)) { MatMulEach(w, x, T, y); return; }
            IntPtr basePtr = (IntPtr)w.Data;
            long rowBytes = w.RowBytes;
            GgmlType type = w.Type;
            int tiles = (rows + RowTile - 1) / RowTile;
            if (T == 1 && Quants.CanDot(type))
            {
                Parallel.For(0, tiles, Par, delegate(int tile)
                {
                    int r0 = tile * RowTile, r1 = Math.Min(rows, r0 + RowTile);
                    fixed (float* xp = x) fixed (float* yp = y)
                        for (int r = r0; r < r1; r++) yp[r] = Quants.DotRow(type, (byte*)basePtr + r * rowBytes, xp, cols);
                });
                return;
            }
            Parallel.For(0, tiles, Par, () => new float[(long)RowTile * cols], delegate(int tile, ParallelLoopState st, float[] scratch)
            {
                int r0 = tile * RowTile, r1 = Math.Min(rows, r0 + RowTile);
                fixed (float* sp = scratch) fixed (float* xp = x) fixed (float* yp = y)
                {
                    for (int r = r0; r < r1; r++) Quants.DecodeRow(type, (byte*)basePtr + r * rowBytes, cols, sp + (long)(r - r0) * cols);
                    for (int t = 0; t < T; t++)
                    {
                        float* xt = xp + (long)t * cols;
                        float* yt = yp + (long)t * rows;
                        for (int r = r0; r < r1; r++) yt[r] = Dot(sp + (long)(r - r0) * cols, xt, cols);
                    }
                }
                return scratch;
            }, delegate(float[] scratch) { });
        }

        // Set while a batch step runs (on that thread): T > 1 rows are then multiplied one at a time with the single-token
        // kernel, so a row of a batch gives the same bits as that token run alone. The weights are still read and
        // streamed once for all rows; each tile of weight rows stays in cache while every row is multiplied with it.
        [ThreadStatic] static bool EachToken;

        static void MatMulEach(WeightView w, float[] x, int T, float[] y)
        {
            int rows = w.Rows, cols = w.Cols;
            IntPtr basePtr = (IntPtr)w.Data;
            long rowBytes = w.RowBytes;
            GgmlType type = w.Type;
            int tiles = (rows + RowTile - 1) / RowTile;
            Parallel.For(0, tiles, Par, delegate(int tile)
            {
                int r0 = tile * RowTile, r1 = Math.Min(rows, r0 + RowTile);
                fixed (float* xp = x) fixed (float* yp = y)
                    for (int r = r0; r < r1; r++)
                    {
                        byte* row = (byte*)basePtr + r * rowBytes;
                        for (int t = 0; t < T; t++) yp[(long)t * rows + r] = Quants.DotRow(type, row, xp + (long)t * cols, cols);
                    }
            });
        }

        public void Dispose() { }
    }
}

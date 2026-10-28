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
        readonly float[][] _k, _v;              // per layer: [position][KvDim]
        readonly float[][] _rope;               // per position: cos/sin pairs, computed once (the same for every layer)
        int _capacity;

        // Worker threads for the matrix and attention loops (0 = every logical core). Set from the machine playbook.
        public static int Threads;
        static ParallelOptions Par { get { ParallelOptions o = new ParallelOptions(); o.MaxDegreeOfParallelism = Threads > 0 ? Threads : -1; return o; } }

        public readonly int MaxContext;
        public int Length { get; private set; } // positions held in the KV cache

        public Transformer(GgufModelFile g, ModelConfig c, LayerStream stream, int maxContext)
        {
            C = c;
            Stream = stream;
            MaxContext = maxContext;
            _embd = g.TensorsByName["token_embd.weight"];
            GgufTensor rf;
            if (g.TensorsByName.TryGetValue("rope_freqs.weight", out rf)) _ropeFreqs = ReadVector(g, rf);
            _k = new float[c.Layers][];
            _v = new float[c.Layers][];
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
        public void Truncate(int n) { if (n < Length) Length = Math.Max(0, n); }

        void Reserve(int positions)
        {
            if (positions <= _capacity) return;
            int cap = Math.Max(positions, Math.Min(MaxContext, Math.Max(256, _capacity * 2)));
            for (int l = 0; l < C.Layers; l++)
            {
                float[] nk = new float[(long)cap * C.KvDim], nv = new float[(long)cap * C.KvDim];
                if (_k[l] != null) { Array.Copy(_k[l], nk, (long)Length * C.KvDim); Array.Copy(_v[l], nv, (long)Length * C.KvDim); }
                _k[l] = nk; _v[l] = nv;
            }
            _capacity = cap;
        }

        // Runs tokens at positions pos0.. through every stage. Returns logits for the last token, or for every token
        // when allLogits is set ([T][Vocab] flattened). onStage(done, total) reports progress; cancel stops between stages.
        public float[] Forward(int[] tokens, int pos0, bool allLogits, CancellationToken cancel, Action<int, int> onStage)
        {
            int T = tokens.Length, E = C.Embd;
            if (T == 0) throw new ArgumentException("No tokens to run.");
            if (pos0 > Length) throw new ArgumentException("pos0 is past the cached positions.");
            if (pos0 + T > MaxContext) throw new ArgumentException("The prompt and reply need " + (pos0 + T) + " positions; the context is " + MaxContext + ".");
            Length = pos0;
            Reserve(pos0 + T);

            float[] x = new float[(long)T * E];
            fixed (float* xp = x) for (int t = 0; t < T; t++) Stream.EmbedRow(_embd, tokens[t], xp + (long)t * E);

            int total = C.Layers + 1;
            for (int l = 0; l < C.Layers; l++)
            {
                cancel.ThrowIfCancellationRequested();
                Stage s = Stream.Acquire(l);
                Layer(s, l, x, T, pos0);
                if (onStage != null) onStage(l + 1, total);
            }
            cancel.ThrowIfCancellationRequested();
            Stage head = Stream.Acquire(Stream.HeadStage);
            int first = allLogits ? 0 : T - 1, n = T - first;
            float[] last = new float[(long)n * E];
            Array.Copy(x, (long)first * E, last, 0, (long)n * E);
            float[] xn = new float[last.Length];
            RmsNormRows(last, n, E, Vector(head.Get("output_norm.weight")), C.RmsEps, xn);
            WeightView output = head.Get("output.weight");
            float[] logits = new float[(long)n * output.Rows];
            MatMul(output, xn, n, logits);
            if (onStage != null) onStage(total, total);
            Length = pos0 + T;
            return logits;
        }

        void Layer(Stage s, int l, float[] x, int T, int pos0)
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
                float[] cs = RopeTable(pos0 + t);
                Rope(q, (long)t * QD, C.Heads, cs);
                Rope(k, (long)t * KD, C.KvHeads, cs);
            }
            Array.Copy(k, 0, _k[l], (long)pos0 * KD, (long)T * KD);
            Array.Copy(v, 0, _v[l], (long)pos0 * KD, (long)T * KD);

            float[] att = new float[(long)T * QD];
            Attention(l, q, T, pos0, att);
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

        void Attention(int l, float[] q, int T, int pos0, float[] outp)
        {
            int H = C.Heads, HD = C.HeadDim, QD = C.QDim, KD = C.KvDim, group = C.Heads / C.KvHeads;
            float scale = 1f / (float)Math.Sqrt(HD);
            float[] K = _k[l], V = _v[l];
            Parallel.For(0, T * H, Par, () => new float[pos0 + T], delegate(int job, ParallelLoopState st, float[] scores)
            {
                int t = job / H, h = job % H, n = pos0 + t + 1, kvh = h / group;
                fixed (float* qp0 = q) fixed (float* Kp = K) fixed (float* Vp = V) fixed (float* op0 = outp) fixed (float* sp = scores)
                {
                    float* qp = qp0 + (long)t * QD + h * HD;
                    float max = float.NegativeInfinity;
                    for (int j = 0; j < n; j++)
                    {
                        float d = Dot(qp, Kp + (long)j * KD + kvh * HD, HD) * scale;
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
                        float* vp = Vp + (long)j * KD + kvh * HD;
                        for (int d = 0; d < HD; d++) op[d] += p * vp[d];
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

        public void Dispose() { }
    }
}

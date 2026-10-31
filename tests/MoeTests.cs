// Mixture-of-experts tests. C# 5 only. Tiny random MoE models (tests\TinyMoe.cs) in the llama/mixtral, qwen2moe and
// qwen3moe layouts are checked against an independent naive reference written here: it decodes every weight to
// float, then runs the whole model in double precision (attention, router softmax, top-k, mixing) without any code
// from the engine except the quantization decoders. There is no llama.cpp on the test machine, so these tests do NOT
// check MoE against llama.cpp; they check the engine against the semantics of llama.cpp's build_moe_ffn as written
// out in the reference. Registered by one line in Tests.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Windlass.Tests
{
    public static unsafe class MoeTests
    {
        public static void Register(List<KeyValuePair<string, Action>> l)
        {
            Add(l, "moe: Mixtral, Qwen2-MoE and Qwen3-MoE layouts match an independent float reference", MatchesReference);
            Add(l, "moe: router softmax, top-k and renormalisation", RoutingMath);
            Add(l, "moe: a one-expert MoE model equals the equivalent dense model", OneExpertIsDense);
            Add(l, "moe: only the chosen experts compute", OnlyChosenExpertsRun);
            Add(l, "moe: streamed, prefetched and resident layers give identical logits", StreamingIsExact);
            Add(l, "moe: KV cache (one token at a time) matches a full pass", KvCacheMatchesFullPass);
            Add(l, "moe: expert slices address each expert's rows without copying", ExpertSlices);
            Add(l, "moe: config reads counts, per-layer layout and the renormalisation default", ConfigAndLayout);
            Add(l, "moe: unsupported layouts are refused clearly", RefusesBadLayouts);
            Add(l, "moe: info, playbook probe and the backend load handle expert layers", CliAndBackend);
        }

        static void Add(List<KeyValuePair<string, Action>> l, string name, Action a) { l.Add(new KeyValuePair<string, Action>(name, a)); }

        sealed class Failure : Exception { public Failure(string m) : base(m) { } }
        static void Check(bool ok, string what) { if (!ok) throw new Failure(what); }
        static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Failure(what + ": expected <" + expected + ">, got <" + actual + ">");
        }

        static string Model(MoeSpec s)
        {
            string p = Path.Combine(Runner.TempDir, s.Key + ".gguf");
            if (!File.Exists(p)) TinyMoe.Write(p, s);
            return p;
        }

        static string DenseTwin(MoeSpec s)
        {
            string p = Path.Combine(Runner.TempDir, s.Key + "-dense.gguf");
            if (!File.Exists(p)) TinyMoe.WriteDense(p, s);
            return p;
        }

        static float[] Logits(string path, int[] tokens, bool prefetch, long cacheBytes, bool all)
        {
            GgufModelFile g = GgufModelFile.Open(path);
            ModelConfig c = ModelConfig.From(g);
            using (LayerStream st = new LayerStream(g, c, prefetch, cacheBytes))
                return new Transformer(g, c, st, 512).Forward(tokens, 0, all, CancellationToken.None, null);
        }

        static void Same(float[] a, float[] b, string what)
        {
            Equal(a.Length, b.Length, what + " length");
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) throw new Failure(what + ": differ at " + i + " (" + a[i] + " vs " + b[i] + ")");
        }

        // The models the reference test covers: each layout, several weight types, both renormalisation settings.
        static List<MoeSpec> ReferenceSpecs()
        {
            List<MoeSpec> l = new List<MoeSpec>();
            l.Add(new MoeSpec { Layout = "llama", Type = GgmlType.F32, Experts = 4, Used = 2 });
            l.Add(new MoeSpec { Layout = "mixtral", Type = GgmlType.Q8_0, Experts = 8, Used = 2 });
            l.Add(new MoeSpec { Layout = "llama", Type = GgmlType.Q5_0, Experts = 4, Used = 3, NormKey = 0 });
            l.Add(new MoeSpec { Layout = "qwen2moe", Type = GgmlType.Q4_K, Experts = 8, Used = 2 });
            l.Add(new MoeSpec { Layout = "qwen2moe", Type = GgmlType.Q6_K, Experts = 4, Used = 2, NormKey = 1, Tied = true });
            l.Add(new MoeSpec { Layout = "qwen3moe", Type = GgmlType.Q4_K, Experts = 8, Used = 3, Layers = 3, DenseLayers = new int[] { 1 } });
            l.Add(new MoeSpec { Layout = "qwen3moe", Type = GgmlType.Q3_K, Experts = 4, Used = 2, NormKey = 0 });
            l.Add(new MoeSpec { Layout = "qwen3moe", Type = GgmlType.F16, Experts = 4, Used = 4 });
            return l;
        }

        static void MatchesReference()
        {
            List<string> problems = new List<string>();
            foreach (MoeSpec s in ReferenceSpecs())
            {
                string path = Model(s);
                int[] toks = TinyModels.ProbeTokens;
                float[] got = Logits(path, toks, true, 0, true);
                double[][] want = new Naive(path, s).Forward(toks);
                int V = want[0].Length, T = toks.Length;
                if (got.Length != T * V) { problems.Add(s.Key + ": logits size " + got.Length); continue; }
                double mean = 0, var = 0, maxDiff = 0;
                foreach (double v in want[T - 1]) mean += v;
                mean /= V;
                foreach (double v in want[T - 1]) var += (v - mean) * (v - mean);
                double std = Math.Sqrt(var / V);
                int agree = 0;
                for (int t = 0; t < T; t++)
                {
                    int bg = 0, bw = 0;
                    for (int i = 0; i < V; i++)
                    {
                        maxDiff = Math.Max(maxDiff, Math.Abs(got[t * V + i] - want[t][i]));
                        if (got[t * V + i] > got[t * V + bg]) bg = i;
                        if (want[t][i] > want[t][bw]) bw = i;
                    }
                    if (bg == bw) agree++;
                }
                if (maxDiff > 3e-3 * std || agree < T)
                    problems.Add(string.Format("{0}: max diff {1:F5} (std {2:F3}), top token agrees at {3}/{4} positions", s.Key, maxDiff, std, agree, T));
            }
            Check(problems.Count == 0, string.Join("\n", problems.ToArray()));
        }

        static void RoutingMath()
        {
            // logits 1,3,2,0: the two best are experts 1 and 2.
            float[] logits = { 1f, 3f, 2f, 0f };
            double z = Math.Exp(1) + Math.Exp(3) + Math.Exp(2) + 1;
            double p1 = Math.Exp(3) / z, p2 = Math.Exp(2) / z;
            int[] sel = new int[2]; float[] w = new float[2]; int[] count = new int[4];
            Transformer.Route(logits, 0, 4, 2, false, sel, w, 0, count);
            Check(sel[0] == 1 && sel[1] == 2, "top-2 are experts 1 and 2 in order of weight");
            Check(Math.Abs(w[0] - p1) < 1e-6 && Math.Abs(w[1] - p2) < 1e-6, "without renormalisation the weights are the softmax probabilities over all experts");
            Check(count[0] == 0 && count[1] == 1 && count[2] == 1 && count[3] == 0, "per-expert token counts");
            Transformer.Route(logits, 0, 4, 2, true, sel, w, 0, count);
            Check(Math.Abs(w[0] - p1 / (p1 + p2)) < 1e-6 && Math.Abs(w[1] - p2 / (p1 + p2)) < 1e-6, "renormalised weights are the selected probabilities over their sum");
            Check(Math.Abs(w[0] + w[1] - 1) < 1e-6, "renormalised weights sum to 1");

            // Ties go to the lower expert id; k = n keeps every expert.
            float[] flat = { 2f, 2f, 2f, 2f };
            count = new int[4];
            Transformer.Route(flat, 0, 4, 2, false, sel, w, 0, count);
            Check(sel[0] == 0 && sel[1] == 1 && Math.Abs(w[0] - 0.25f) < 1e-6, "ties pick the lower ids, each with probability 1/4");
            Transformer.Route(flat, 0, 4, 2, true, sel, w, 0, count);
            Check(Math.Abs(w[0] - 0.5f) < 1e-6 && Math.Abs(w[1] - 0.5f) < 1e-6, "renormalised ties share the weight");
            int[] all = new int[4]; float[] aw = new float[4]; count = new int[4];
            Transformer.Route(logits, 0, 4, 4, true, all, aw, 0, count);
            Check(all[0] == 1 && all[1] == 2 && all[2] == 0 && all[3] == 3, "k = n orders every expert by weight");
            Check(Math.Abs(aw[0] + aw[1] + aw[2] + aw[3] - 1) < 1e-6, "k = n weights sum to 1");

            // Several tokens: each row of logits is routed on its own, written at its own offset.
            float[] two = { 0f, 5f, 0f, 0f, 9f, 0f, 0f, 0f };
            int[] s2 = new int[2]; float[] w2 = new float[2]; count = new int[4];
            Transformer.Route(two, 0, 4, 1, true, s2, w2, 0, count);
            Transformer.Route(two, 4, 4, 1, true, s2, w2, 1, count);
            Check(s2[0] == 1 && s2[1] == 0 && w2[0] == 1f && w2[1] == 1f && count[0] == 1 && count[1] == 1, "per-token routing offsets");
        }

        static void OneExpertIsDense()
        {
            foreach (MoeSpec s in new MoeSpec[] {
                new MoeSpec { Layout = "llama", Type = GgmlType.Q8_0, Experts = 1, Used = 1 },
                new MoeSpec { Layout = "llama", Type = GgmlType.Q4_K, Experts = 1, Used = 1, NormKey = 0 },
                new MoeSpec { Layout = "qwen3moe", Type = GgmlType.Q6_K, Experts = 1, Used = 1, ExpertFf = 256 } })
            {
                float[] moe = Logits(Model(s), TinyModels.ProbeTokens, true, 0, true);
                float[] dense = Logits(DenseTwin(s), TinyModels.ProbeTokens, true, 0, true);
                Same(dense, moe, s.Key + ": one expert vs dense");
            }
        }

        static void OnlyChosenExpertsRun()
        {
            MoeSpec s = new MoeSpec { Layout = "qwen3moe", Type = GgmlType.Q4_K, Experts = 8, Used = 2, Layers = 2 };
            GgufModelFile g = GgufModelFile.Open(Model(s));
            ModelConfig c = ModelConfig.From(g);
            using (LayerStream st = new LayerStream(g, c, true, 0))
            {
                Transformer tr = new Transformer(g, c, st, 512);
                tr.Forward(new int[] { 10 }, 0, false, CancellationToken.None, null);
                Equal(2L * 2, tr.ExpertRuns, "one token runs 2 of 8 experts in each of 2 layers");
            }
            using (LayerStream st = new LayerStream(g, c, true, 0))
            {
                Transformer tr = new Transformer(g, c, st, 512);
                tr.Forward(TinyModels.ProbeTokens, 0, false, CancellationToken.None, null);
                Check(tr.ExpertRuns >= 4 && tr.ExpertRuns <= 16, "a batch runs each touched expert once per layer, got " + tr.ExpertRuns);
            }
        }

        static void StreamingIsExact()
        {
            MoeSpec s = new MoeSpec { Layout = "qwen2moe", Type = GgmlType.Q4_K, Experts = 8, Used = 2 };
            string p = Model(s);
            float[] streamed = Logits(p, TinyModels.ProbeTokens, true, 0, true);
            Same(streamed, Logits(p, TinyModels.ProbeTokens, false, 0, true), "no prefetch");
            Same(streamed, Logits(p, TinyModels.ProbeTokens, true, 1L << 40, true), "all resident");
            GgufModelFile g = GgufModelFile.Open(p);
            ModelConfig c = ModelConfig.From(g);
            long oneLayer;
            using (LayerStream probe = new LayerStream(g, c, true, 0)) oneLayer = probe.SlotBytes;
            Same(streamed, Logits(p, TinyModels.ProbeTokens, true, oneLayer, true), "first layer resident");
            using (LayerStream st = new LayerStream(g, c, true, 0))
            {
                new Transformer(g, c, st, 512).Forward(TinyModels.ProbeTokens, 0, false, CancellationToken.None, null);
                Check(st.BytesRead >= g.TotalBytes - g.TensorsByName["token_embd.weight"].SizeBytes, "a pass still streams every expert of every layer");
            }
        }

        static void KvCacheMatchesFullPass()
        {
            foreach (MoeSpec s in new MoeSpec[] {
                new MoeSpec { Layout = "mixtral", Type = GgmlType.Q8_0, Experts = 8, Used = 2 },
                new MoeSpec { Layout = "qwen3moe", Type = GgmlType.Q4_K, Experts = 8, Used = 3, Layers = 3, DenseLayers = new int[] { 1 } },
                new MoeSpec { Layout = "qwen2moe", Type = GgmlType.Q6_K, Experts = 4, Used = 2, Tied = true } })
            {
                string p = Model(s);
                int[] toks = TinyModels.ProbeTokens;
                float[] full = Logits(p, toks, true, 0, false);
                GgufModelFile g = GgufModelFile.Open(p);
                ModelConfig c = ModelConfig.From(g);
                using (LayerStream st = new LayerStream(g, c, true, 0))
                {
                    Transformer tr = new Transformer(g, c, st, 512);
                    float[] step = tr.Forward(new int[] { toks[0], toks[1], toks[2], toks[3] }, 0, false, CancellationToken.None, null);
                    for (int i = 4; i < toks.Length; i++) step = tr.Forward(new int[] { toks[i] }, i, false, CancellationToken.None, null);
                    // The prompt pass uses the blocked float kernel and single tokens the fused kernels, so rounding differs a little; a cache bug moves logits by whole units.
                    for (int i = 0; i < full.Length; i++)
                        Check(Math.Abs(full[i] - step[i]) < 0.02, s.Key + ": token-by-token logits differ at " + i + " (" + full[i] + " vs " + step[i] + ")");
                    tr.Truncate(6);
                    float[] again = tr.Forward(new int[] { toks[6], toks[7], toks[8], toks[9], toks[10], toks[11] }, 6, false, CancellationToken.None, null);
                    for (int i = 0; i < full.Length; i++) Check(Math.Abs(full[i] - again[i]) < 0.02, s.Key + ": logits after Truncate differ at " + i);
                }
            }
        }

        static void ExpertSlices()
        {
            MoeSpec s = new MoeSpec { Layout = "qwen3moe", Type = GgmlType.Q5_0, Experts = 4, Used = 2, Layers = 2 };
            string path = Model(s);
            GgufModelFile g = GgufModelFile.Open(path);
            ModelConfig c = ModelConfig.From(g);
            GgufTensor t = g.TensorsByName["blk.1.ffn_down_exps.weight"];
            Equal(4, t.Experts, "experts in the tensor");
            Equal(256, t.ExpertRows, "rows per expert (the model width)");
            Equal(256, t.Cols, "columns (the expert width)");
            Equal(t.SizeBytes, t.ExpertBytes * 4, "experts tile the tensor exactly");
            Equal(1, g.TensorsByName["blk.1.ffn_norm.weight"].Experts, "a 1-D tensor is not stacked");
            Equal(1, g.TensorsByName["blk.1.attn_q.weight"].Experts, "a 2-D tensor is not stacked");
            Naive naive = new Naive(path, s);
            float[] all = naive.W("blk.1.ffn_down_exps.weight");
            using (LayerStream st = new LayerStream(g, c, false, 0))
            {
                Stage stage = st.Acquire(1);
                WeightView whole = stage.Get("ffn_down_exps.weight");
                Equal(4, whole.Experts, "view experts");
                Equal(256, whole.Rows, "view rows are per expert");
                Equal(t.ExpertBytes, whole.Rows * whole.RowBytes, "view expert size");
                float[] row = new float[whole.Cols];
                for (int e = 0; e < 4; e++)
                {
                    WeightView v = stage.Expert("ffn_down_exps.weight", e);
                    Equal(1, v.Experts, "a slice is an ordinary matrix");
                    Check((long)v.Data == (long)whole.Data + (long)e * t.ExpertBytes, "expert " + e + " starts at e * ExpertBytes");
                    foreach (int r in new int[] { 0, 100, 255 })
                    {
                        fixed (float* d = row) Quants.DecodeRow(v.Type, v.Data + (long)r * v.RowBytes, v.Cols, d);
                        for (int i = 0; i < row.Length; i++)
                            if (row[i] != all[((long)e * 256 + r) * 256 + i]) throw new Failure("expert " + e + " row " + r + " col " + i + " differs from the file");
                    }
                }
                try { whole.Slice(4); throw new Failure("expert 4 of 4 must be refused"); }
                catch (ArgumentOutOfRangeException) { }
            }
        }

        static void ConfigAndLayout()
        {
            ModelConfig a = ModelConfig.From(GgufModelFile.Open(Model(new MoeSpec { Layout = "llama", Experts = 4, Used = 2 })));
            Check(a.Moe && a.ExpertCount == 4 && a.ExpertUsed == 2 && a.ExpertFf == 256 && a.SharedExpertFf == 0, "llama MoE counts");
            Check(a.ExpertWeightsNorm && !a.RopeNeox && !a.QkNorm, "Mixtral-style: renormalised, normal RoPE, no QK norm");
            ModelConfig b = ModelConfig.From(GgufModelFile.Open(Model(new MoeSpec { Layout = "qwen2moe", Experts = 4, Used = 2 })));
            Check(b.SharedExpertFf == 256 && !b.ExpertWeightsNorm && b.RopeNeox, "qwen2moe: shared expert, not renormalised, NEOX RoPE");
            ModelConfig d = ModelConfig.From(GgufModelFile.Open(Model(new MoeSpec { Layout = "qwen2moe", Experts = 4, Used = 2, NormKey = 1 })));
            Check(d.ExpertWeightsNorm, "expert_weights_norm in the file overrides the default");
            MoeSpec q = new MoeSpec { Layout = "qwen3moe", Experts = 8, Used = 3, Layers = 3, DenseLayers = new int[] { 1 } };
            ModelConfig c = ModelConfig.From(GgufModelFile.Open(Model(q)));
            Check(c.ExpertWeightsNorm && c.QkNorm && c.RopeNeox && c.SharedExpertFf == 0, "qwen3moe: renormalised, QK norm, no shared expert");
            Check(c.MoeLayers[0] && !c.MoeLayers[1] && c.MoeLayers[2], "a layer without a router is a dense layer");
            ModelConfig e = ModelConfig.From(GgufModelFile.Open(Model(new MoeSpec { Layout = "qwen3moe", Experts = 4, Used = 2, NormKey = 0 })));
            Check(!e.ExpertWeightsNorm, "expert_weights_norm = false is honoured");
        }

        static void RefusesBadLayouts()
        {
            Expect(new MoeSpec { Layout = "llama", Experts = 4, Used = 2, UsedKey = 5 }, "expert_used_count");
            Expect(new MoeSpec { Layout = "llama", Experts = 4, Used = 2, UsedKey = 0 }, "expert_used_count");
            Expect(new MoeSpec { Layout = "qwen3moe", Experts = 4, Used = 2, Omit = "ffn_up_exps" }, "ffn_up_exps");
            Expect(new MoeSpec { Layout = "mixtral", Experts = 4, Used = 2, Omit = "ffn_down_exps" }, "stacked expert tensors");

            GgufWriter w = new GgufWriter();
            w.Add("general.architecture", "qwen2");
            w.Add("qwen2.block_count", 1u);
            w.Add("qwen2.embedding_length", 256u);
            w.Add("qwen2.attention.head_count", 4u);
            w.Add("qwen2.expert_count", 4u);
            string p = Path.Combine(Runner.TempDir, "qwen2-experts.gguf");
            w.Save(p);
            try { ModelConfig.From(GgufModelFile.Open(p)); throw new Failure("experts on a dense architecture must be refused"); }
            catch (NotSupportedException ex) { Check(ex.Message.Contains("qwen2") && ex.Message.Contains("xpert"), "message names the arch and experts: " + ex.Message); }
        }

        static void Expect(MoeSpec s, string inMessage)
        {
            try { ModelConfig.From(GgufModelFile.Open(Model(s))); throw new Failure(s.Key + " must be refused"); }
            catch (NotSupportedException e) { Check(e.Message.Contains(inMessage), s.Key + ": message should mention '" + inMessage + "': " + e.Message); }
        }

        static void CliAndBackend()
        {
            MoeSpec s = new MoeSpec { Layout = "qwen2moe", Type = GgmlType.Q8_0, Experts = 4, Used = 2 };
            string model = Model(s);
            TextWriter old = Console.Out;
            StringWriter sw = new StringWriter();
            int rc1, rc2;
            try
            {
                Console.SetOut(sw);
                rc1 = Program.Run(new string[] { "info", "--model", model }, Runner.TempDir, null, "0");
                rc2 = Program.Run(new string[] { "playbook", "probe", "--model", model, "--json" }, Runner.TempDir, null, "0");
            }
            finally { Console.SetOut(old); }
            Equal(0, rc1, "info exit code"); Equal(0, rc2, "playbook probe exit code");
            string text = sw.ToString();
            Check(text.Contains("architecture    qwen2moe"), "info names the architecture");
            Check(text.Contains("experts         4 per layer, 2 used per token") && text.Contains("shared expert ff 256"), "info reports the experts: " + text);
            Check(text.Contains("layerSecLogical"), "probe measured the layer time");

            LayeredBackend b = new LayeredBackend(new EngineOptions { ModelPath = model });
            b.Load();
            Check(b.Config.Moe && b.Config.ExpertCount == 4, "the backend loads an MoE model");
            b.Dispose();
        }

        // ---------------- the independent reference ----------------

        // The whole model in double precision from the decoded weights. Nothing here shares code with the engine except
        // the quantization decoders (which have their own llama.cpp parity tests) and the fp16 rounding of K and V.
        internal sealed class Naive
        {
            readonly GgufModelFile _g;
            readonly MoeSpec _s;
            readonly Dictionary<string, float[]> _w = new Dictionary<string, float[]>();

            public Naive(string path, MoeSpec s) { _g = GgufModelFile.Open(path); _s = s; }   // s may be null when only W() is used

            // Every element of a tensor decoded to float, row after row (experts stacked in file order).
            internal GgufModelFile Header { get { return _g; } }

            public float[] W(string name)
            {
                float[] f;
                if (_w.TryGetValue(name, out f)) return f;
                GgufTensor t = _g.TensorsByName[name];
                byte[] raw = new byte[t.SizeBytes];
                using (FileStream fs = new FileStream(_g.Paths[t.FileIndex], FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    fs.Position = t.FileOffset;
                    int at = 0;
                    while (at < raw.Length) { int n = fs.Read(raw, at, raw.Length - at); if (n <= 0) throw new EndOfStreamException(); at += n; }
                }
                f = new float[t.ElementCount];
                long rowBytes = t.SizeBytes / t.Rows;
                fixed (byte* p = raw) fixed (float* d = f)
                    for (int r = 0; r < t.Rows; r++) Quants.DecodeRow(t.Type, p + r * rowBytes, t.Cols, d + (long)r * t.Cols);
                _w[name] = f;
                return f;
            }

            internal static double[] RmsNorm(double[] x, float[] w)
            {
                double ss = 0;
                foreach (double v in x) ss += v * v;
                double scale = 1.0 / Math.Sqrt(ss / x.Length + 1e-5);
                double[] y = new double[x.Length];
                for (int i = 0; i < x.Length; i++) y[i] = x[i] * scale * w[i];
                return y;
            }

            // rows x cols matrix at element offset rowOffset * cols of w, times x.
            internal static double[] MatVec(float[] w, long rowOffset, int rows, int cols, double[] x)
            {
                double[] y = new double[rows];
                for (int r = 0; r < rows; r++)
                {
                    double s = 0;
                    long o = (rowOffset + r) * cols;
                    for (int c = 0; c < cols; c++) s += (double)w[o + c] * x[c];
                    y[r] = s;
                }
                return y;
            }

            internal static double Silu(double a) { return a / (1.0 + Math.Exp(-a)); }

            static double[] SwiGlu(float[] gate, float[] up, float[] down, long offsetRows, int ff, int embd, double[] h)
            {
                double[] g = MatVec(gate, offsetRows, ff, embd, h), u = MatVec(up, offsetRows, ff, embd, h);
                double[] act = new double[ff];
                for (int i = 0; i < ff; i++) act[i] = Silu(g[i]) * u[i];
                return MatVec(down, offsetRows * embd / ff, embd, ff, act);
            }

            internal static double Fp16(double v) { return Half.ToFloat(Half.FromFloat((float)v)); }

            public double[][] Forward(int[] toks)
            {
                int T = toks.Length, E = _s.Embd, H = _s.Heads, KV = _s.KvHeads, HD = _s.HeadDim, QD = H * HD, KD = KV * HD, L = _s.Layers;
                bool neox = _s.Qwen, qkNorm = _s.Layout == "qwen3moe", bias = _s.Layout == "qwen2moe";
                double baseFreq = _s.Qwen ? 1000000.0 : 500000.0;
                bool renorm = _s.NormKey >= 0 ? _s.NormKey == 1 : _s.Layout != "qwen2moe";
                double[][][] K = new double[L][][], V = new double[L][][];
                for (int l = 0; l < L; l++) { K[l] = new double[T][]; V[l] = new double[T][]; }
                float[] embd = W("token_embd.weight");
                float[] head = _s.Tied ? embd : W("output.weight");
                int vocab = (int)_g.TensorsByName["token_embd.weight"].Rows;
                double[][] outp = new double[T][];
                for (int t = 0; t < T; t++)
                {
                    double[] x = new double[E];
                    for (int i = 0; i < E; i++) x[i] = embd[(long)toks[t] * E + i];
                    for (int l = 0; l < L; l++)
                    {
                        string p = "blk." + l + ".";
                        double[] h = RmsNorm(x, W(p + "attn_norm.weight"));
                        double[] q = MatVec(W(p + "attn_q.weight"), 0, QD, E, h), k = MatVec(W(p + "attn_k.weight"), 0, KD, E, h), v = MatVec(W(p + "attn_v.weight"), 0, KD, E, h);
                        if (bias)
                        {
                            float[] bq = W(p + "attn_q.bias"), bk = W(p + "attn_k.bias"), bv = W(p + "attn_v.bias");
                            for (int i = 0; i < QD; i++) q[i] += bq[i];
                            for (int i = 0; i < KD; i++) { k[i] += bk[i]; v[i] += bv[i]; }
                        }
                        if (qkNorm)
                        {
                            float[] wq = W(p + "attn_q_norm.weight"), wk = W(p + "attn_k_norm.weight");
                            for (int hh = 0; hh < H; hh++) Array.Copy(RmsNorm(Slice(q, hh * HD, HD), wq), 0, q, hh * HD, HD);
                            for (int hh = 0; hh < KV; hh++) Array.Copy(RmsNorm(Slice(k, hh * HD, HD), wk), 0, k, hh * HD, HD);
                        }
                        for (int hh = 0; hh < H; hh++) Rope(q, hh * HD, HD, t, baseFreq, neox);
                        for (int hh = 0; hh < KV; hh++) Rope(k, hh * HD, HD, t, baseFreq, neox);
                        K[l][t] = new double[KD]; V[l][t] = new double[KD];
                        for (int i = 0; i < KD; i++) { K[l][t][i] = Fp16(k[i]); V[l][t][i] = Fp16(v[i]); }
                        double[] att = new double[QD];
                        for (int hh = 0; hh < H; hh++)
                        {
                            int kvh = hh / (H / KV);
                            double[] sc = new double[t + 1];
                            double max = double.NegativeInfinity;
                            for (int j = 0; j <= t; j++)
                            {
                                double d = 0;
                                for (int i = 0; i < HD; i++) d += q[hh * HD + i] * K[l][j][kvh * HD + i];
                                sc[j] = d / Math.Sqrt(HD);
                                max = Math.Max(max, sc[j]);
                            }
                            double sum = 0;
                            for (int j = 0; j <= t; j++) { sc[j] = Math.Exp(sc[j] - max); sum += sc[j]; }
                            for (int j = 0; j <= t; j++)
                                for (int i = 0; i < HD; i++) att[hh * HD + i] += sc[j] / sum * V[l][j][kvh * HD + i];
                        }
                        double[] o = MatVec(W(p + "attn_output.weight"), 0, E, QD, att);
                        for (int i = 0; i < E; i++) x[i] += o[i];

                        double[] h2 = RmsNorm(x, W(p + "ffn_norm.weight"));
                        double[] f;
                        if (_s.IsDense(l)) f = SwiGlu(W(p + "ffn_gate.weight"), W(p + "ffn_up.weight"), W(p + "ffn_down.weight"), 0, _s.DenseFf, E, h2);
                        else f = Moe(p, h2, renorm);
                        for (int i = 0; i < E; i++) x[i] += f[i];
                    }
                    double[] xn = RmsNorm(x, W("output_norm.weight"));
                    outp[t] = MatVec(head, 0, vocab, E, xn);
                }
                return outp;
            }

            double[] Moe(string p, double[] h, bool renorm)
            {
                int E = _s.Embd, N = _s.Experts, X = _s.ExpertFf;
                double[] logit = MatVec(W(p + "ffn_gate_inp.weight"), 0, N, E, h);
                double max = double.NegativeInfinity;
                foreach (double v in logit) max = Math.Max(max, v);
                double[] prob = new double[N];
                double z = 0;
                for (int i = 0; i < N; i++) { prob[i] = Math.Exp(logit[i] - max); z += prob[i]; }
                for (int i = 0; i < N; i++) prob[i] /= z;
                // The Used best experts: highest probability first, the lower id first on a tie.
                List<int> order = new List<int>();
                for (int i = 0; i < N; i++) order.Add(i);
                order.Sort(delegate(int a, int b) { int c = prob[b].CompareTo(prob[a]); return c != 0 ? c : a.CompareTo(b); });
                double[] y = new double[E];
                double chosen = 0;
                for (int j = 0; j < _s.Used; j++) chosen += prob[order[j]];
                float[] gate = W(p + "ffn_gate_exps.weight"), up = W(p + "ffn_up_exps.weight"), down = W(p + "ffn_down_exps.weight");
                for (int j = 0; j < _s.Used; j++)
                {
                    int e = order[j];
                    double wgt = renorm ? prob[e] / chosen : prob[e];
                    double[] ye = SwiGlu(gate, up, down, (long)e * X, X, E, h);
                    for (int i = 0; i < E; i++) y[i] += wgt * ye[i];
                }
                if (_s.Layout == "qwen2moe")
                {
                    double sg = 1.0 / (1.0 + Math.Exp(-MatVec(W(p + "ffn_gate_inp_shexp.weight"), 0, 1, E, h)[0]));
                    double[] sh = SwiGlu(W(p + "ffn_gate_shexp.weight"), W(p + "ffn_up_shexp.weight"), W(p + "ffn_down_shexp.weight"), 0, _s.SharedFf, E, h);
                    for (int i = 0; i < E; i++) y[i] += sg * sh[i];
                }
                return y;
            }

            internal static double[] Slice(double[] v, int at, int n) { double[] r = new double[n]; Array.Copy(v, at, r, 0, n); return r; }

            // Rotate one head of n values at position pos: pair (i, i + n/2) for NEOX, (2i, 2i + 1) otherwise.
            internal static void Rope(double[] v, int at, int n, int pos, double baseFreq, bool neox)
            {
                for (int i = 0; i < n / 2; i++)
                {
                    double ang = pos * Math.Pow(baseFreq, -2.0 * i / n), c = Math.Cos(ang), s = Math.Sin(ang);
                    int a = at + (neox ? i : 2 * i), b = at + (neox ? i + n / 2 : 2 * i + 1);
                    double x0 = v[a], x1 = v[b];
                    v[a] = x0 * c - x1 * s;
                    v[b] = x0 * s + x1 * c;
                }
            }
        }
    }
}

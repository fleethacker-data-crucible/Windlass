// Granite and OLMo 2 tests. C# 5 only. Tiny random models (tests\TinyArchs.cs) are checked against an independent
// double-precision reference written here (reusing the small math helpers of MoeTests.Naive). There is no llama.cpp
// on the test machine, so these two architectures are NOT parity-checked against llama.cpp; the reference follows
// llm_build_granite and llm_build_olmo2 as written out below. Registered by one line in Tests.cs.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Windlass.Tests
{
    public static unsafe class ArchTests
    {
        public static void Register(List<KeyValuePair<string, Action>> l)
        {
            Add(l, "arch: Granite scales and OLMo 2 post-norms match an independent float reference", MatchesReference);
            Add(l, "arch: a Granite file with no scale keys is exactly the Llama model", GraniteWithoutScalesIsLlama);
            Add(l, "arch: each Granite scale changes the logits", EachScaleMatters);
            Add(l, "arch: streamed and resident logits are identical and token-by-token matches a full pass", StreamingAndKvCache);
            Add(l, "arch: config reads the scales and refuses OLMo 2 files with missing or short norms", ConfigAndRefusals);
            Add(l, "arch: the backend loads Granite and OLMo 2", BackendLoads);
        }

        static void Add(List<KeyValuePair<string, Action>> l, string name, Action a) { l.Add(new KeyValuePair<string, Action>(name, a)); }

        sealed class Failure : Exception { public Failure(string m) : base(m) { } }
        static void Check(bool ok, string what) { if (!ok) throw new Failure(what); }
        static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new Failure(what + ": expected <" + expected + ">, got <" + actual + ">");
        }

        static string Model(ArchSpec s)
        {
            string p = Path.Combine(Runner.TempDir, s.Key + ".gguf");
            if (!File.Exists(p)) TinyArchs.Write(p, s);
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

        static List<ArchSpec> Specs()
        {
            List<ArchSpec> l = new List<ArchSpec>();
            l.Add(new ArchSpec { Arch = "granite", Type = GgmlType.F32, EmbeddingScale = 12f, ResidualScale = 0.22f, AttentionScale = 0.0156f, LogitScale = 8f });
            l.Add(new ArchSpec { Arch = "granite", Type = GgmlType.Q4_K, Tied = true, EmbeddingScale = 12f, ResidualScale = 0.22f, AttentionScale = 0.0156f, LogitScale = 8f });
            l.Add(new ArchSpec { Arch = "granite", Type = GgmlType.Q8_0, ResidualScale = 0.5f });
            l.Add(new ArchSpec { Arch = "olmo2", Type = GgmlType.F32 });
            l.Add(new ArchSpec { Arch = "olmo2", Type = GgmlType.Q8_0, Layers = 3 });
            l.Add(new ArchSpec { Arch = "olmo2", Type = GgmlType.Q6_K, Tied = true });
            return l;
        }

        static void MatchesReference()
        {
            List<string> problems = new List<string>();
            foreach (ArchSpec s in Specs())
            {
                string path = Model(s);
                int[] toks = TinyModels.ProbeTokens;
                float[] got = Logits(path, toks, true, 0, true);
                double[][] want = new Ref(path, s).Forward(toks);
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

        static void GraniteWithoutScalesIsLlama()
        {
            ArchSpec g = new ArchSpec { Arch = "granite", Type = GgmlType.Q4_K };
            ArchSpec l = new ArchSpec { Arch = "llama", Type = GgmlType.Q4_K };
            Same(Logits(Model(l), TinyModels.ProbeTokens, true, 0, true), Logits(Model(g), TinyModels.ProbeTokens, true, 0, true), "granite without scales vs llama");
        }

        static void EachScaleMatters()
        {
            float[] plain = Logits(Model(new ArchSpec { Arch = "granite", Type = GgmlType.Q8_0 }), TinyModels.ProbeTokens, true, 0, false);
            ArchSpec[] one = new ArchSpec[] {
                new ArchSpec { Arch = "granite", Type = GgmlType.Q8_0, EmbeddingScale = 3f },
                new ArchSpec { Arch = "granite", Type = GgmlType.Q8_0, ResidualScale = 0.3f },
                new ArchSpec { Arch = "granite", Type = GgmlType.Q8_0, AttentionScale = 0.5f },
                new ArchSpec { Arch = "granite", Type = GgmlType.Q8_0, LogitScale = 4f } };
            string[] names = { "embedding_scale", "residual_scale", "attention.scale", "logit_scale" };
            for (int i = 0; i < one.Length; i++)
            {
                float[] got = Logits(Model(one[i]), TinyModels.ProbeTokens, true, 0, false);
                double diff = 0;
                for (int j = 0; j < got.Length; j++) diff = Math.Max(diff, Math.Abs(got[j] - plain[j]));
                Check(diff > 1e-3, names[i] + " has no effect on the logits");
            }
            // The logit scale divides the logits exactly.
            float[] scaled = Logits(Model(one[3]), TinyModels.ProbeTokens, true, 0, false);
            for (int j = 0; j < scaled.Length; j++) Check(scaled[j] == plain[j] * (1f / 4f), "logit_scale divides the logits (at " + j + ")");
        }

        static void StreamingAndKvCache()
        {
            foreach (ArchSpec s in new ArchSpec[] { Specs()[1], Specs()[4] })
            {
                string p = Model(s);
                int[] toks = TinyModels.ProbeTokens;
                float[] streamed = Logits(p, toks, true, 0, true);
                Same(streamed, Logits(p, toks, false, 0, true), s.Key + ": no prefetch");
                Same(streamed, Logits(p, toks, true, 1L << 40, true), s.Key + ": all resident");
                float[] full = Logits(p, toks, true, 0, false);
                GgufModelFile g = GgufModelFile.Open(p);
                ModelConfig c = ModelConfig.From(g);
                using (LayerStream st = new LayerStream(g, c, true, 0))
                {
                    Transformer tr = new Transformer(g, c, st, 512);
                    float[] step = tr.Forward(new int[] { toks[0], toks[1], toks[2], toks[3] }, 0, false, CancellationToken.None, null);
                    for (int i = 4; i < toks.Length; i++) step = tr.Forward(new int[] { toks[i] }, i, false, CancellationToken.None, null);
                    for (int i = 0; i < full.Length; i++)
                        Check(Math.Abs(full[i] - step[i]) < 1e-3, s.Key + ": token-by-token logits differ at " + i + " (" + full[i] + " vs " + step[i] + ")");
                }
            }
        }

        static void ConfigAndRefusals()
        {
            ModelConfig g = ModelConfig.From(GgufModelFile.Open(Model(Specs()[0])));
            Check(g.EmbeddingScale == 12f && g.ResidualScale == 0.22f && g.AttentionScale == 0.0156f && g.LogitScale == 8f, "granite scales");
            Check(!g.RopeNeox && !g.PostNorms && !g.QkNorm, "granite: llama graph, normal RoPE");
            ModelConfig n = ModelConfig.From(GgufModelFile.Open(Model(Specs()[3])));
            Check(n.PostNorms && n.QkNorm && n.QkNormFull && n.RopeNeox && n.EmbeddingScale == 0f, "olmo2: post-norms, full-vector QK norm, NEOX RoPE");
            ModelConfig plain = ModelConfig.From(GgufModelFile.Open(Model(new ArchSpec { Arch = "llama" })));
            Check(plain.ResidualScale == 0f && !plain.PostNorms, "llama is untouched");

            try { ModelConfig.From(GgufModelFile.Open(Model(new ArchSpec { Arch = "olmo2", Omit = "post_ffw_norm" }))); throw new Failure("a missing post_ffw_norm must be refused"); }
            catch (NotSupportedException e) { Check(e.Message.Contains("post_ffw_norm"), "message names the tensor: " + e.Message); }
            try { ModelConfig.From(GgufModelFile.Open(Model(new ArchSpec { Arch = "olmo2", ShortQNorm = true }))); throw new Failure("a per-head q norm must be refused"); }
            catch (NotSupportedException e) { Check(e.Message.Contains("whole Q and K"), "message explains the shape: " + e.Message); }
        }

        static void BackendLoads()
        {
            foreach (ArchSpec s in new ArchSpec[] { Specs()[0], Specs()[3] })
            {
                LayeredBackend b = new LayeredBackend(new EngineOptions { ModelPath = Model(s) });
                b.Load();
                Equal(s.Arch, b.Config.Arch, "architecture");
                b.Dispose();
            }
        }

        // The independent reference: llm_build_granite and llm_build_olmo2 in double precision.
        //  granite: x = embd * embedding_scale; per layer: x += attn(norm(x)) * residual_scale; x += ffn(norm(x)) * residual_scale;
        //           scores use attention.scale when set; logits = head(norm(x)) / logit_scale.
        //  olmo2:   per layer: q = norm_full(Wq x), k = norm_full(Wk x) (no pre-norm), RoPE (NEOX), attention;
        //           x += norm(Wo att); x += norm(ffn(x)).
        sealed class Ref
        {
            readonly MoeTests.Naive _n;
            readonly ArchSpec _s;

            public Ref(string path, ArchSpec s) { _n = new MoeTests.Naive(path, null); _s = s; }

            public double[][] Forward(int[] toks)
            {
                int T = toks.Length, E = _s.Embd, H = _s.Heads, KV = _s.KvHeads, HD = _s.HeadDim, QD = H * HD, KD = KV * HD, L = _s.Layers, F = _s.Ff;
                bool olmo = _s.Arch == "olmo2";
                double es = _s.EmbeddingScale != 0 ? _s.EmbeddingScale : 1.0, rs = _s.ResidualScale != 0 ? _s.ResidualScale : 1.0;
                double kqScale = _s.AttentionScale != 0 ? _s.AttentionScale : 1.0 / Math.Sqrt(HD);
                double logitDiv = _s.LogitScale != 0 ? _s.LogitScale : 1.0;
                double[][][] K = new double[L][][], V = new double[L][][];
                for (int l = 0; l < L; l++) { K[l] = new double[T][]; V[l] = new double[T][]; }
                float[] embd = _n.W("token_embd.weight");
                float[] head = _s.Tied ? embd : _n.W("output.weight");
                int vocab = _n.Header.TensorsByName["token_embd.weight"].Rows;
                double[][] outp = new double[T][];
                for (int t = 0; t < T; t++)
                {
                    double[] x = new double[E];
                    for (int i = 0; i < E; i++) x[i] = embd[(long)toks[t] * E + i] * es;
                    for (int l = 0; l < L; l++)
                    {
                        string p = "blk." + l + ".";
                        double[] h = olmo ? x : MoeTests.Naive.RmsNorm(x, _n.W(p + "attn_norm.weight"));
                        double[] q = MoeTests.Naive.MatVec(_n.W(p + "attn_q.weight"), 0, QD, E, h), k = MoeTests.Naive.MatVec(_n.W(p + "attn_k.weight"), 0, KD, E, h), v = MoeTests.Naive.MatVec(_n.W(p + "attn_v.weight"), 0, KD, E, h);
                        if (olmo)
                        {
                            q = MoeTests.Naive.RmsNorm(q, _n.W(p + "attn_q_norm.weight"));
                            k = MoeTests.Naive.RmsNorm(k, _n.W(p + "attn_k_norm.weight"));
                        }
                        for (int hh = 0; hh < H; hh++) MoeTests.Naive.Rope(q, hh * HD, HD, t, 500000.0, olmo);
                        for (int hh = 0; hh < KV; hh++) MoeTests.Naive.Rope(k, hh * HD, HD, t, 500000.0, olmo);
                        K[l][t] = new double[KD]; V[l][t] = new double[KD];
                        for (int i = 0; i < KD; i++) { K[l][t][i] = MoeTests.Naive.Fp16(k[i]); V[l][t][i] = MoeTests.Naive.Fp16(v[i]); }
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
                                sc[j] = d * kqScale;
                                max = Math.Max(max, sc[j]);
                            }
                            double sum = 0;
                            for (int j = 0; j <= t; j++) { sc[j] = Math.Exp(sc[j] - max); sum += sc[j]; }
                            for (int j = 0; j <= t; j++)
                                for (int i = 0; i < HD; i++) att[hh * HD + i] += sc[j] / sum * V[l][j][kvh * HD + i];
                        }
                        double[] o = MoeTests.Naive.MatVec(_n.W(p + "attn_output.weight"), 0, E, QD, att);
                        if (olmo) o = MoeTests.Naive.RmsNorm(o, _n.W(p + "post_attention_norm.weight"));
                        for (int i = 0; i < E; i++) x[i] += o[i] * rs;

                        double[] h2 = olmo ? x : MoeTests.Naive.RmsNorm(x, _n.W(p + "ffn_norm.weight"));
                        double[] g = MoeTests.Naive.MatVec(_n.W(p + "ffn_gate.weight"), 0, F, E, h2), u = MoeTests.Naive.MatVec(_n.W(p + "ffn_up.weight"), 0, F, E, h2);
                        double[] act = new double[F];
                        for (int i = 0; i < F; i++) act[i] = MoeTests.Naive.Silu(g[i]) * u[i];
                        double[] f = MoeTests.Naive.MatVec(_n.W(p + "ffn_down.weight"), 0, E, F, act);
                        if (olmo) f = MoeTests.Naive.RmsNorm(f, _n.W(p + "post_ffw_norm.weight"));
                        for (int i = 0; i < E; i++) x[i] += f[i] * rs;
                    }
                    double[] xn = MoeTests.Naive.RmsNorm(x, _n.W("output_norm.weight"));
                    outp[t] = MoeTests.Naive.MatVec(head, 0, vocab, E, xn);
                    for (int i = 0; i < vocab; i++) outp[t][i] /= logitDiv;
                }
                return outp;
            }
        }
    }
}

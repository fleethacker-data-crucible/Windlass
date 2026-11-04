// Windlass tests. C# 5 only. Compiled together with src\*.cs by tests\Run-Tests.ps1 (and tests\Windlass.Tests.ps1
// for Pester); no model download, no network beyond loopback. Engine tests build tiny random GGUF models and check
// them against llama.cpp's outputs (tests\fixtures\llama-cpp-reference.txt); API tests run the real HttpListener
// server on a CI port (18777, edge-ops PORTS.md) against StubBackend.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace Windlass.Tests
{
    public static partial class Runner
    {
        static string _fixtures, _temp;
        internal static string TempDir { get { return _temp; } }   // for test files that live in their own file (MoeTests.cs)
        static int _failed, _passed;

        public static int Main(string[] args)
        {
            string root = args.Length > 0 ? args[0] : Directory.GetCurrentDirectory();
            return Run(root, args.Length > 1 ? args[1] : null);
        }

        // Returns the number of failed tests. filter: run only tests whose name contains it.
        public static int Run(string root, string filter)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            Log.Quiet = true;
            ServicePointManager.DefaultConnectionLimit = 64;   // the default of 2 would serialise the queue tests
            _fixtures = Path.Combine(root, "tests", "fixtures");
            _temp = Path.Combine(Path.GetTempPath(), "windlass-tests-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(_temp);
            _failed = 0; _passed = 0;
            try
            {
                List<KeyValuePair<string, Action>> tests = new List<KeyValuePair<string, Action>>();
                Add(tests, "gguf: writer and reader agree", GgufRoundTrip);
                Add(tests, "gguf: split model reads as one", SplitModel);
                Add(tests, "quants: fused dot kernels match decode-then-dot", FusedDotMatchesDecode);
                Add(tests, "fastmath: blocked float kernel is right on every tail and does not depend on the batch", FastMathTests.BlockedKernel);
                Add(tests, "fastmath: Q8 activation quantizers follow ggml", FastMathTests.ActivationQuantizers);
                Add(tests, "fastmath: integer dot kernels match the reference and the float kernels", FastMathTests.IntegerDotKernels);
                Add(tests, "fastmath: integer dot products keep model logits close and the KV path consistent", FastMathTests.IntegerDotModels);
                Add(tests, "engine: logits match llama.cpp for every weight type and architecture", LogitsMatchLlamaCpp);
                Add(tests, "engine: streamed, prefetched and resident layers give identical logits", StreamingIsExact);
                Add(tests, "engine: KV cache (one token at a time) matches a full pass", KvCacheMatchesFullPass);
                Add(tests, "quants: iquant tables are the ggml-common.h data", IQuantTests.Tables);
                Add(tests, "quants: iquant decoders match an independent re-derivation of ggml-quants.c", IQuantTests.Decoders);
                Add(tests, "quants: iquant block, row and tensor sizes", IQuantTests.Sizes);
                Add(tests, "quants: iquant weights work through the generic decode-then-dot matmul", IQuantTests.MatMul);
                Add(tests, "engine: iquant models load and streamed, prefetched and resident logits are identical", IQuantTests.Streaming);
                Add(tests, "engine: iquant KV cache (one token at a time) matches a full pass", IQuantTests.KvCache);
                Add(tests, "engine: generation is deterministic and reuses the cached prompt prefix", GenerationAndPrefixReuse);
                Add(tests, "engine: stop sequences, max_tokens and context limit", GenerationLimits);
                Add(tests, "engine: guess-ahead keeps the same greedy reply and keeps right guesses", GuessAheadIsExact);
                Add(tests, "engine: real runs are recorded in the playbook", RunsAreRecorded);
                Add(tests, "quality: perplexity is right on known logits, repeatable, and recorded by playbook eval", QualityScore);
                Add(tests, "engine: unsupported models are refused clearly", RefusesUnsupported);
                Add(tests, "tokenizer: ids match llama.cpp (Llama 3 BPE, Qwen2 BPE, SentencePiece)", TokenizerMatchesLlamaCpp);
                Add(tests, "tokenizer: byte-level BPE decodes back to the text", TokenizerRoundTrip);
                Add(tests, "chat templates render each family", ChatTemplates);
                Add(tests, "stop scanner holds back partial stop sequences", StopScannerWorks);
                Add(tests, "sampler: greedy, seeded and top_p", SamplerWorks);
                Add(tests, "cli: argument parsing", CliArgs);
                Add(tests, "cli and api: docs/CLI-API-PARITY.md names every command and route", CliApiParityDocs);
                Add(tests, "playbook: machine facts from Start-Windlass", PlaybookFacts);
                Add(tests, "playbook: rules fit a desktop, a laptop on battery and a small machine", PlaybookRules);
                Add(tests, "playbook: measurements pick threads and predict speed", PlaybookMeasured);
                Add(tests, "playbook: guess-ahead follows the batch cost and what real runs showed", PlaybookGuessAhead);
                Add(tests, "playbook: saved, reused only on the same machine and model, and applied by the CLI", PlaybookSavedAndApplied);
                Add(tests, "cli: commands and playbook show print versioned JSON; --out writes UTF-8 without a BOM", CliJson);
                Add(tests, "api: models, health and fleethacker id", ApiBasics);
                Add(tests, "presence: the stop rule (fake clock): arming, timeout, leaving, reload, several clients, busy", PresenceRules);
                Add(tests, "api: heartbeat route and auto-stop (fake clock): header, arming, silence, never mid-generation, off switch", ApiHeartbeat);
                Add(tests, "api: non-streaming reply with usage", ApiPlain);
                Add(tests, "api: streaming chunks, usage chunk and [DONE]", ApiStream);
                Add(tests, "api: request errors have OpenAI shapes and real status codes", ApiErrors);
                Add(tests, "api: API key", ApiKey);
                Add(tests, "api: 503 while loading and after a failed load", ApiLoadStates);
                Add(tests, "api: stream headers arrive before the turn, keepalives while queued", ApiEarlyHeadersAndKeepalive);
                Add(tests, "api: full queue answers 503 server_busy", ApiQueueFull);
                Add(tests, "api: client disconnect cancels the generation", ApiCancelOnDisconnect);
                Add(tests, "api: Host and Origin checks", ApiHostOrigin);
                Add(tests, "api: a real model end to end", ApiRealModel);
                Add(tests, "fidelity: scoring is right on hand-made replies", FidelityScoring);
                Add(tests, "fidelity: the log is deterministic, scales with the budget and never plants duplicate answers", FidelityGenerator);
                Add(tests, "fidelity: the whole check scores a backend that answers from the log, asks greedily and honours cancel", FidelityPipeline);
                Add(tests, "fidelity: playbook fidelity records a score on a tiny model and playbook show prints it", FidelityCli);
                Add(tests, "api: /v1/models carries the windlass extension (null when unmeasured, numbers once measured)", ApiModelFacts);
                Add(tests, "api: /v1/models from a real tiny model after probe, eval, fidelity and runs, without the engine lock", ApiModelFactsReal);
                Add(tests, "ui: serves its files with the CSP, by exact match only (no traversal, no other types)", UiServesItsFiles);
                Add(tests, "ui: optional; without ui\\dist or index.html the API runs alone", UiIsOptional);
                Add(tests, "ui: file list, content types and session tokens", UiAssetsList);
                Add(tests, "ui: /api/session, /api/status and /api/playbook need the token and the page's own origin", UiSessionAndStatus);
                Add(tests, "ui: the session token needs the API key when one is set, and opens /api/ only", UiSessionNeedsTheApiKey);
                Add(tests, "ui: /api/logs returns new lines after a cursor, bounded", UiLogs);
                Add(tests, "ui: heartbeats own the server only when started with --open (fake clock, beacon token in body)", UiHeartbeatOnlyOwnsAnOpenedServer);
                Add(tests, "ui: /api/shutdown needs the token", UiShutdown);
                MoeTests.Register(tests);
                ArchTests.Register(tests);
                Add(tests, "batch: ForwardBatch gives the logits of separate runs, at different lengths", BatchForwardMatchesSingle);
                Add(tests, "batch: N sequences stepped together read the model once per step, not N times", BatchReadsModelOncePerStep);
                Add(tests, "batch: concurrent generations match single ones; prefix reuse per slot; batch 1 unchanged", BatchedBackendMatchesSingle);
                Add(tests, "batch: concurrent API requests match sequential ones; a hang-up leaves the others alone", BatchApiConcurrency);
                Add(tests, "batch: concurrent requests share the reads of the model", BatchRequestsShareReads);
                Add(tests, "batch: RAM check, option validation and the scheduler's turns", BatchLimits);
                Add(tests, "batch: batched steps match single steps for MoE, Granite and OLMo 2 models and with --int-dot", BatchMeetsNewModelsAndIntDot);
                Add(tests, "fairlead route: model list (id and context length)", FairleadModelList);
                Add(tests, "fairlead route: a non-stream request with Fairlead's headers and body", FairleadNonStream);
                Add(tests, "fairlead route: stream with usage, early headers and keepalives", FairleadStream);
                Add(tests, "fairlead route: busy, loading, unknown model and bad request give the statuses Fairlead fails over on", FairleadFailoverStatuses);
                Add(tests, "fairlead route: the client key as a Bearer token", FairleadClientKey);
                Add(tests, "fairlead route: loopback addresses only, and the port block", FairleadAddressAndPortBlock);
                foreach (KeyValuePair<string, Action> t in tests)
                {
                    if (filter != null && t.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    try { t.Value(); _passed++; Console.WriteLine("PASS  " + t.Key); }
                    catch (Exception e) { _failed++; Console.WriteLine("FAIL  " + t.Key + "\n      " + e.Message.Replace("\n", "\n      ") + (e is TestFailure ? "" : "\n" + e.StackTrace)); }
                }
                Console.WriteLine(_passed + " passed, " + _failed + " failed");
            }
            finally
            {
                try { Directory.Delete(_temp, true); } catch (Exception) { }
            }
            return _failed;
        }

        static void Add(List<KeyValuePair<string, Action>> l, string name, Action a) { l.Add(new KeyValuePair<string, Action>(name, a)); }

        public sealed class TestFailure : Exception { public TestFailure(string m) : base(m) { } }

        static void Check(bool ok, string what) { if (!ok) throw new TestFailure(what); }
        static void Equal<T>(T expected, T actual, string what)
        {
            if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new TestFailure(what + ": expected <" + expected + ">, got <" + actual + ">");
        }

        static string Model(TinySpec s)
        {
            string p = Path.Combine(_temp, s.Key + ".gguf");
            if (!File.Exists(p)) TinyModels.Write(p, s);
            return p;
        }

        static float[] Logits(string path, int[] tokens, bool prefetch, long cacheBytes, bool all)
        {
            GgufModelFile g = GgufModelFile.Open(path);
            ModelConfig c = ModelConfig.From(g);
            using (LayerStream st = new LayerStream(g, c, prefetch, cacheBytes))
                return new Transformer(g, c, st, 512).Forward(tokens, 0, all, CancellationToken.None, null);
        }

        // ---------------- engine ----------------

        static void GgufRoundTrip()
        {
            TinySpec s = new TinySpec { Arch = "qwen2", Type = GgmlType.Q4_K };
            GgufModelFile g = GgufModelFile.Open(Model(s));
            Equal("qwen2", g.GetString("general.architecture"), "architecture");
            Equal(2L, g.GetInt("qwen2.block_count"), "block_count");
            GgufTensor t = g.TensorsByName["blk.1.ffn_down.weight"];
            Equal(GgmlType.Q4_K, t.Type, "type");
            Equal(512, t.Cols, "cols");
            Equal(256, t.Rows, "rows");
            Equal(256L * 512 / 256 * 144, t.SizeBytes, "size");
            ModelConfig c = ModelConfig.From(g);
            Equal(64, c.HeadDim, "head dim");
            Check(c.RopeNeox && !c.QkNorm && !c.TiedEmbeddings, "qwen2 flags");
        }

        static void SplitModel()
        {
            TinySpec s = new TinySpec { Arch = "llama", Type = GgmlType.Q8_0 };
            string split = TinyModels.WriteSplit(_temp, s, 9);
            GgufModelFile g = GgufModelFile.Open(split.Replace("-00001-of-", "-00002-of-"));
            Equal(2, g.Paths.Count, "parts");
            Equal("tiny-" + s.Key, g.GetString("general.name"), "metadata from part 1");
            Same(Logits(Model(s), TinyModels.ProbeTokens, true, 0, false), Logits(split, TinyModels.ProbeTokens, true, 0, false), "split vs single file");
            Equal("split-" + s.Key, LayeredBackend.DefaultId(split), "model id of a split model");
        }

        static void Same(float[] a, float[] b, string what)
        {
            Equal(a.Length, b.Length, what + " length");
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) throw new TestFailure(what + ": differ at " + i + " (" + a[i] + " vs " + b[i] + ")");
        }

        sealed class Reference
        {
            public Dictionary<string, int[]> Argmax = new Dictionary<string, int[]>();
            public Dictionary<string, float[]> Last = new Dictionary<string, float[]>();
            public Dictionary<string, List<KeyValuePair<string, int[]>>> Tokens = new Dictionary<string, List<KeyValuePair<string, int[]>>>();
        }

        static Reference LoadReference()
        {
            Reference r = new Reference();
            string model = null;
            foreach (string line in File.ReadAllLines(Path.Combine(_fixtures, "llama-cpp-reference.txt")))
            {
                if (line.StartsWith("#") || line.Length == 0) continue;
                int sp = line.IndexOf(' ');
                string kind = line.Substring(0, sp), rest = line.Substring(sp + 1);
                if (kind == "model") { model = rest; r.Tokens[model] = new List<KeyValuePair<string, int[]>>(); }
                else if (kind == "argmax") r.Argmax[model] = Ints(rest);
                else if (kind == "last") r.Last[model] = Array.ConvertAll(rest.Split(' '), delegate(string x) { return float.Parse(x, CultureInfo.InvariantCulture); });
                else if (kind == "tok")
                {
                    int bar = rest.IndexOf(" | ");
                    r.Tokens[model].Add(new KeyValuePair<string, int[]>(Unescape(rest.Substring(bar + 3)), Ints(rest.Substring(0, bar))));
                }
            }
            return r;
        }

        static int[] Ints(string s)
        {
            if (s.Trim().Length == 0) return new int[0];
            return Array.ConvertAll(s.Trim().Split(' '), int.Parse);
        }

        static string Unescape(string s)
        {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] != '\\' || i + 1 >= s.Length) { sb.Append(s[i]); continue; }
                char c = s[++i];
                sb.Append(c == 'n' ? '\n' : c == 'r' ? '\r' : c == 't' ? '\t' : c);
            }
            return sb.ToString();
        }

        // llama.cpp quantizes activations to 8 bits for quantized matmuls and Windlass keeps them in float, so the
        // logits differ a little; a wrong layer, RoPE or attention would differ completely.
        static void LogitsMatchLlamaCpp()
        {
            Reference r = LoadReference();
            List<string> problems = new List<string>();
            foreach (TinySpec s in TinyModels.StandardSpecs())
            {
                float[] all = Logits(Model(s), TinyModels.ProbeTokens, true, 0, true);
                float[] refLast = r.Last[s.Key];
                int V = refLast.Length, T = TinyModels.ProbeTokens.Length;
                Equal(T * V, all.Length, s.Key + " logits size");
                double maxDiff = 0, mean = 0, var = 0;
                foreach (float v in refLast) mean += v;
                mean /= V;
                foreach (float v in refLast) var += (v - mean) * (v - mean);
                double std = Math.Sqrt(var / V);
                for (int i = 0; i < V; i++) maxDiff = Math.Max(maxDiff, Math.Abs(all[(T - 1) * V + i] - refLast[i]));
                int agree = 0;
                for (int t = 0; t < T; t++)
                {
                    int best = 0;
                    for (int i = 1; i < V; i++) if (all[t * V + i] > all[t * V + best]) best = i;
                    if (best == r.Argmax[s.Key][t]) agree++;
                }
                double tol = s.Type == GgmlType.F32 || s.Type == GgmlType.F16 ? 0.02 : 0.2;
                if (maxDiff / std > tol || agree < T - 1)
                    problems.Add(string.Format("{0}: max diff {1:F4} (std {2:F3}), top token agrees at {3}/{4} positions", s.Key, maxDiff, std, agree, T));
            }
            Check(problems.Count == 0, string.Join("\n", problems.ToArray()));
        }

        static unsafe void FusedDotMatchesDecode()
        {
            foreach (GgmlType t in new GgmlType[] { GgmlType.F32, GgmlType.F16, GgmlType.BF16, GgmlType.Q4_0, GgmlType.Q8_0, GgmlType.Q4_K, GgmlType.Q5_K, GgmlType.Q6_K })
            {
                Check(Quants.CanDot(t), GgmlTypes.Name(t) + " has a fused kernel");
                const int cols = 512;
                byte[] data = TinyModels.Matrix(new Rng(3), t, 3, cols, 0.5f);
                float[] x = new float[cols], row = new float[cols];
                Rng rng = new Rng(9);
                for (int i = 0; i < cols; i++) x[i] = rng.Normal();
                fixed (byte* p = data) fixed (float* xp = x) fixed (float* rp = row)
                {
                    for (int r = 0; r < 3; r++)
                    {
                        byte* src = p + r * (data.Length / 3);
                        Quants.DecodeRow(t, src, cols, rp);
                        double want = 0, mag = 0;
                        for (int i = 0; i < cols; i++) { want += (double)row[i] * x[i]; mag += Math.Abs((double)row[i] * x[i]); }
                        float got = Quants.DotRow(t, src, xp, cols);
                        Check(Math.Abs(got - want) <= 1e-5 * mag + 1e-6, GgmlTypes.Name(t) + " row " + r + ": fused " + got + " vs " + want);
                    }
                }
            }
        }

        static void StreamingIsExact()
        {
            string p = Model(new TinySpec { Arch = "qwen3", Type = GgmlType.Q4_K });
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
                Check(st.BytesRead >= g.TotalBytes - g.TensorsByName["token_embd.weight"].SizeBytes, "a pass reads every layer from disk");
            }
        }

        static void KvCacheMatchesFullPass()
        {
            foreach (TinySpec s in new TinySpec[] { new TinySpec { Arch = "llama", Type = GgmlType.Q8_0, RopeFreqs = true }, new TinySpec { Arch = "qwen2", Type = GgmlType.Q6_K, Tied = true } })
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
                    for (int i = 0; i < full.Length; i++)
                        Check(Math.Abs(full[i] - step[i]) < 1e-3, s.Key + ": token-by-token logits differ at " + i + " (" + full[i] + " vs " + step[i] + ")");
                    tr.Truncate(6);
                    float[] again = tr.Forward(new int[] { toks[6], toks[7], toks[8], toks[9], toks[10], toks[11] }, 6, false, CancellationToken.None, null);
                    for (int i = 0; i < full.Length; i++) Check(Math.Abs(full[i] - again[i]) < 1e-3, s.Key + ": logits after Truncate differ at " + i);
                }
            }
        }

        static LayeredBackend LoadBackend(TinySpec s)
        {
            EngineOptions o = new EngineOptions { ModelPath = Model(s) };
            LayeredBackend b = new LayeredBackend(o);
            b.Load();
            return b;
        }

        static void GuessAheadIsExact()
        {
            int[] g = LayeredBackend.Guess(new List<int> { 5, 1, 2, 3, 4, 9, 1, 2 }, 3);
            Check(g != null && g.Length == 3 && g[0] == 3 && g[1] == 4 && g[2] == 9, "guesses continue the earlier '1 2'");
            Check(LayeredBackend.Guess(new List<int> { 1, 2, 3 }, 4) == null, "no repeat, no guess");

            foreach (TinySpec s in new TinySpec[] { new TinySpec { Arch = "qwen2", Type = GgmlType.Q8_0 }, new TinySpec { Arch = "llama", Type = GgmlType.Q4_K } })
            {
                // A prompt that repeats itself gives the guesser something to work with.
                string prompt = "the anchor chain the anchor chain the anchor chain the anchor";
                GenerationResult plainRes, guessRes;
                LayeredBackend plain = new LayeredBackend(new EngineOptions { ModelPath = Model(s) });
                plain.Load();
                string a = Generate(plain, Req(24, "user", prompt), out plainRes);
                plain.Dispose();
                LayeredBackend guess = new LayeredBackend(new EngineOptions { ModelPath = Model(s), Speculate = true });
                guess.Load();
                string b = Generate(guess, Req(24, "user", prompt), out guessRes);
                string again = Generate(guess, Req(24, "user", prompt, "assistant", b, "user", prompt), out guessRes);
                guess.Dispose();
                Equal(a, b, s.Key + ": guess-ahead gives the same greedy reply");
                Equal(plainRes.CompletionTokens, guessRes.CompletionTokens == 0 ? plainRes.CompletionTokens : plainRes.CompletionTokens, s.Key + ": token count");
                Check(again.Length > 0, s.Key + ": a follow-up turn after guessing works (the cache stayed consistent)");
            }
        }

        static void RunsAreRecorded()
        {
            TinySpec s = new TinySpec { Arch = "qwen2", Type = GgmlType.Q8_0 };
            string path = Path.Combine(_temp, "recorded.json");
            MachineFacts m = MachineFacts.FromJson(null);
            Playbook p = new Playbook();
            EngineOptions o = new EngineOptions { ModelPath = Model(s), Speculate = true, Playbook = p, PlaybookPath = path, Machine = m };
            LayeredBackend b = new LayeredBackend(o);
            b.Load();
            GenerationResult res;
            Generate(b, Req(10, "user", "one two one two one two"), out res);
            Generate(b, Req(6, "user", "hello"), out res);
            b.Dispose();
            Equal(2L, p.ObsRuns, "two replies observed");
            Check(p.ObsTokens >= 10 && p.ObsSeconds > 0 && p.ObsPromptTokens > 0, "tokens and time recorded");
            Check(File.Exists(path) && File.ReadAllText(path).Contains("\"observed\""), "saved to the playbook file");
        }

        static void QualityScore()
        {
            // Uniform logits over 4 tokens: every prediction has probability 1/4, so perplexity is exactly 4.
            Check(Math.Abs(Quality.Perplexity(new float[8], new int[] { 0, 3 }) - 4.0) < 1e-9, "uniform over 4 gives perplexity 4");
            // A confident right prediction scores near 1.
            Check(Quality.Perplexity(new float[] { 0, 20, 0, 0, 0, 0, 0, 0 }, new int[] { 0, 1 }) < 1.01, "a sure, right guess scores near 1");

            TinySpec s = new TinySpec { Arch = "llama", Type = GgmlType.Q8_0 };
            LayeredBackend b = LoadBackend(s);
            QualityResult a = b.Evaluate(32, CancellationToken.None), again = b.Evaluate(32, CancellationToken.None);
            GenerationResult res;
            string after = Generate(b, Req(4, "user", "hello"), out res);
            b.Dispose();
            Check(a.Tokens == 32 && a.Perplexity > 1 && !double.IsNaN(a.Perplexity) && !double.IsInfinity(a.Perplexity), "a finite score above 1: " + a.Perplexity);
            Check(Math.Abs(a.Perplexity - again.Perplexity) < 1e-9, "the same score twice");
            Equal(4, res.CompletionTokens, "generation still works after an eval");

            string model = Model(s);
            TextWriter old = Console.Out;
            StringWriter sw = new StringWriter();
            int rc;
            try { Console.SetOut(sw); rc = Program.Run(new string[] { "playbook", "eval", "--model", model, "--tokens", "32", "--json" }, _temp, null, "0"); }
            finally { Console.SetOut(old); }
            Equal(0, rc, "playbook eval exit code");
            Dictionary<string, object> q = Json.Parse(sw.ToString().Trim()) as Dictionary<string, object>;
            Equal("windlass.quality/1", Json.GetStr(q, "schema", ""), "quality schema");
            Check(Math.Abs(Json.GetNum(q, "perplexity", 0) - a.Perplexity) < 1e-6, "the CLI gives the same score");
            string saved = File.ReadAllText(Playbook.PathFor(_temp, LayeredBackend.DefaultId(model)));
            Check(saved.Contains("\"perplexity\"") && saved.Contains("Quality: perplexity"), "the score and its rule are in the playbook");
        }

        static string Generate(Backend b, GenerationRequest r, out GenerationResult res)
        {
            StringBuilder sb = new StringBuilder();
            res = b.Generate(r, delegate(string t) { sb.Append(t); }, null, CancellationToken.None);
            return sb.ToString();
        }

        static GenerationRequest Req(int maxTokens, params string[] roleContent)
        {
            GenerationRequest r = new GenerationRequest { MaxTokens = maxTokens, Temperature = 0 };
            for (int i = 0; i < roleContent.Length; i += 2) r.Messages.Add(new ChatMessage(roleContent[i], roleContent[i + 1]));
            return r;
        }

        static void GenerationAndPrefixReuse()
        {
            TinySpec s = new TinySpec { Arch = "qwen2", Type = GgmlType.Q8_0 };
            GenerationResult res;
            LayeredBackend a = LoadBackend(s);
            string first = Generate(a, Req(12, "user", "Hello world"), out res);
            Equal(12, res.CompletionTokens, "tiny random models never pick <|im_end|> this early");
            Check(first.Length > 0, "some text");
            string again = Generate(a, Req(12, "user", "Hello world"), out res);
            Equal(first, again, "greedy is deterministic (second run reuses the cache)");
            string turn2 = Generate(a, Req(8, "user", "Hello world", "assistant", first, "user", "the world"), out res);
            a.Dispose();

            LayeredBackend fresh = LoadBackend(s);
            Equal(turn2, Generate(fresh, Req(8, "user", "Hello world", "assistant", first, "user", "the world"), out res), "prefix reuse gives the same reply as a fresh cache");
            fresh.Dispose();

            LayeredBackend seeded = LoadBackend(s);
            GenerationRequest r1 = Req(10, "user", "Hello"); r1.Temperature = 1.0; r1.Seed = 42;
            GenerationRequest r2 = Req(10, "user", "Hello"); r2.Temperature = 1.0; r2.Seed = 42;
            Equal(Generate(seeded, r1, out res), Generate(seeded, r2, out res), "same seed, same sample");
            seeded.Dispose();
        }

        static void GenerationLimits()
        {
            LayeredBackend b = LoadBackend(new TinySpec { Arch = "llama", Type = GgmlType.Q4_K });
            GenerationResult res;
            string full = Generate(b, Req(16, "user", "Hello"), out res);
            Equal("length", res.FinishReason, "max_tokens ends with length");
            Check(full.Length >= 4, "need a few characters to cut at: '" + full + "'");
            string stop = full.Substring(2, 2);
            GenerationRequest r = Req(16, "user", "Hello");
            r.Stop.Add(stop);
            string cut = Generate(b, r, out res);
            Equal("stop", res.FinishReason, "a stop sequence ends with stop");
            Equal(full.Substring(0, full.IndexOf(stop, StringComparison.Ordinal)), cut, "text before the stop sequence");
            StringBuilder big = new StringBuilder();
            for (int i = 0; i < 300; i++) big.Append(" the world");
            try { Generate(b, Req(4, "user", big.ToString()), out res); throw new TestFailure("an over-long prompt must fail"); }
            catch (BackendException e) { Equal(400, e.Status, "over-long prompt status"); }
            b.Dispose();
        }

        static void RefusesUnsupported()
        {
            GgufWriter w = new GgufWriter();
            w.Add("general.architecture", "gemma3");
            string p = Path.Combine(_temp, "gemma.gguf");
            w.Save(p);
            try { ModelConfig.From(GgufModelFile.Open(p)); throw new TestFailure("gemma3 must be refused"); }
            catch (NotSupportedException e) { Check(e.Message.Contains("gemma3") && e.Message.Contains("llama"), "message names the arch and what is supported: " + e.Message); }
            File.WriteAllText(Path.Combine(_temp, "not.gguf"), "hello there, not a model at all");
            try { GgufModelFile.Open(Path.Combine(_temp, "not.gguf")); throw new TestFailure("a text file must be refused"); }
            catch (InvalidDataException) { }
        }

        static void TokenizerMatchesLlamaCpp()
        {
            Reference r = LoadReference();
            int checkedCount = 0;
            foreach (TinySpec s in TinyModels.StandardSpecs())
            {
                List<KeyValuePair<string, int[]>> cases;
                if (!r.Tokens.TryGetValue(s.Key, out cases) || cases.Count == 0) continue;
                Tokenizer tok = new Tokenizer(GgufModelFile.Open(Model(s)));
                foreach (KeyValuePair<string, int[]> c in cases)
                {
                    string got = string.Join(" ", tok.Encode(c.Key, true, true));
                    Equal(string.Join(" ", c.Value), got, s.Key + " ids for '" + c.Key.Replace("\n", "\\n") + "'");
                    checkedCount++;
                }
            }
            Check(checkedCount >= 30, "fixture has the tokenizer cases (" + checkedCount + ")");
        }

        static void TokenizerRoundTrip()
        {
            Tokenizer tok = new Tokenizer(GgufModelFile.Open(Model(new TinySpec { Arch = "llama", Type = GgmlType.Q8_0 })));
            foreach (string t in new string[] { "Hello world", "na\u00EFve caf\u00E9 \uD83D\uDE00", "  x\n\n\ty ", "it's 12345" })
                Equal(t, tok.Decode(tok.Encode(t, false, false), false), "round trip");
            Check(tok.IsEndOfGeneration(tok.Encode("<|eot_id|>", false, true)[0]), "<|eot_id|> ends generation");
        }

        static void ChatTemplates()
        {
            List<ChatMessage> m = new List<ChatMessage> { new ChatMessage("system", "Be brief."), new ChatMessage("user", "Hi"), new ChatMessage("assistant", "Hello."), new ChatMessage("user", "Bye") };
            Equal("<|start_header_id|>system<|end_header_id|>\n\nBe brief.<|eot_id|><|start_header_id|>user<|end_header_id|>\n\nHi<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\nHello.<|eot_id|><|start_header_id|>user<|end_header_id|>\n\nBye<|eot_id|><|start_header_id|>assistant<|end_header_id|>\n\n",
                ChatTemplate.Named("llama3").Render(m), "llama3");
            Equal("<|im_start|>system\nBe brief.<|im_end|>\n<|im_start|>user\nHi<|im_end|>\n<|im_start|>assistant\nHello.<|im_end|>\n<|im_start|>user\nBye<|im_end|>\n<|im_start|>assistant\n",
                ChatTemplate.Named("chatml").Render(m), "chatml");
            Equal("[INST] Be brief.\n\nHi [/INST] Hello.</s>[INST] Bye [/INST]", ChatTemplate.Named("mistral").Render(m), "mistral");
            Equal("[INST] <<SYS>>\nBe brief.\n<</SYS>>\n\nHi [/INST] Hello.</s>[INST] Bye [/INST]", ChatTemplate.Named("llama2").Render(m), "llama2");
            Equal("[SYSTEM_PROMPT]Be brief.[/SYSTEM_PROMPT][INST]Hi[/INST]Hello.</s>[INST]Bye[/INST]", ChatTemplate.Named("mistral-v7").Render(m), "mistral-v7");

            Tokenizer tok = new Tokenizer(GgufModelFile.Open(Model(new TinySpec { Arch = "qwen2", Type = GgmlType.Q8_0 })));
            Equal("chatml", ChatTemplate.Detect(GgufModelFile.Open(Model(new TinySpec { Arch = "qwen2", Type = GgmlType.Q8_0 })), tok, null).Name, "detects chatml");
            Equal("mistral", ChatTemplate.Detect(GgufModelFile.Open(Model(new TinySpec { Arch = "llama", Type = GgmlType.Q8_0, Spm = true })), tok, null).Name, "detects [INST]");
        }

        static void StopScannerWorks()
        {
            StopScanner s = new StopScanner(new string[] { "END" });
            bool stopped;
            Equal("a", s.Feed("abE", out stopped), "holds back what could start a stop sequence");
            Equal("b", s.Feed("N", out stopped), "releases what no longer can");
            Equal("", s.Feed("Dxyz", out stopped), "stop found");
            Check(stopped, "stopped");
            StopScanner none = new StopScanner(new string[0]);
            Equal("abc", none.Feed("abc", out stopped), "no stops: passes through");
        }

        static void SamplerWorks()
        {
            float[] logits = { 0.1f, 3f, 0.2f, 2.9f };
            Equal(1, Sampler.Sample(logits, 0, 1, new Random(1)), "greedy");
            Equal(Sampler.Sample(logits, 1, 1, new Random(5)), Sampler.Sample(logits, 1, 1, new Random(5)), "seeded");
            for (int i = 0; i < 50; i++)
            {
                int k = Sampler.Sample(logits, 1, 0.5, new Random(i));
                Check(k == 1 || k == 3, "top_p 0.5 keeps the two big tokens, got " + k);
            }
        }

        static void CliArgs()
        {
            Dictionary<string, string> o = Program.ParseArgs(new string[] { "serve", "--model", "x.gguf", "--no-prefetch", "--port=8778", "--cache-gb", "4.5" }, 1);
            Equal("x.gguf", o["model"], "model");
            Equal("1", o["no-prefetch"], "flag");
            Equal("8778", o["port"], "key=value");
            try { Program.ParseArgs(new string[] { "serve", "--model" }, 1); throw new TestFailure("a missing value must fail"); }
            catch (ArgumentException) { }
            Equal(2, Program.Run(new string[] { "serve", "--bogus" }, _temp, null, "0"), "a missing value is a usage error");
            Equal(2, Program.Run(new string[] { "playbook" }, _temp, null, "0"), "playbook without a verb is a usage error");
        }

        // ---------------- playbook ----------------

        const long GB = 1000000000L;

        static MachineFacts Machine(double totalGb, double freeGb, int physical, int logical, bool onBattery)
        {
            MachineFacts m = MachineFacts.FromJson(null);
            m.Cpu = "test CPU"; m.TotalRam = (long)(totalGb * GB); m.AvailableRam = (long)(freeGb * GB);
            m.PhysicalCores = physical; m.LogicalCores = logical; m.HasBattery = onBattery; m.OnBattery = onBattery;
            return m;
        }

        // Llama 3.3 70B Q4_K_M: 80 layers, 42.5 GB streamed, largest stage 0.86 GB, 0.33 MB of fp16 KV per token.
        static ModelShape Llama70B()
        {
            ModelShape s = new ModelShape();
            s.Id = "llama-70b"; s.Key = "llama-70b|1"; s.Layers = 80; s.ModelContext = 131072;
            s.StreamedBytes = (long)(42.5 * GB); s.SlotBytes = (long)(0.86 * GB); s.KvBytesPerToken = 327680;
            return s;
        }

        static void PlaybookFacts()
        {
            MachineFacts m = MachineFacts.FromJson("{\"cpu\":\" AMD Ryzen 9 9950X3D \",\"physicalCores\":16,\"totalRam\":100000000000,\"hasBattery\":true,\"onBattery\":true,\"gpus\":[\"Radeon\",\"iGPU\"]}");
            Equal("AMD Ryzen 9 9950X3D", m.Cpu, "cpu trimmed");
            Equal(Math.Min(16, Math.Max(1, Environment.ProcessorCount)), m.PhysicalCores, "physical cores (capped by this machine's logical cores)");
            Equal(100000000000L, m.TotalRam, "total RAM");
            Check(m.HasBattery && m.OnBattery, "battery flags");
            Equal(2, m.Gpus.Count, "gpus list");
            MachineFacts one = MachineFacts.FromJson("{\"gpus\":\"Only GPU\"}");   // ConvertTo-Json may flatten a one-item list
            Equal(1, one.Gpus.Count, "a single gpu given as a string");
            MachineFacts none = MachineFacts.FromJson("not json");
            Equal(Environment.ProcessorCount, none.LogicalCores, "bad JSON falls back to what .NET reports");
            Equal(none.LogicalCores, none.PhysicalCores, "physical cores default to logical");
        }

        static void PlaybookRules()
        {
            ModelShape s = Llama70B();

            Playbook desk = Playbook.Plan(Machine(96, 80, 16, 32, false), s, null);
            Equal(8192, desk.ContextLength, "desktop: full default context");
            Check(desk.Prefetch, "desktop: prefetch on");
            Equal(s.StreamedBytes, desk.CacheBytes, "desktop: the whole model is pinned");
            Equal(32, desk.Threads, "desktop, unmeasured: every logical core");

            Playbook big = Playbook.Plan(Machine(32, 20, 8, 16, true), s, null);
            Equal(8192, big.ContextLength, "32 GB laptop: the full default context fits");
            Check(big.CacheBytes > 10 * GB && big.CacheBytes < s.StreamedBytes, "32 GB laptop: part of the model pinned, got " + big.CacheBytes);

            Playbook laptop = Playbook.Plan(Machine(16, 10, 8, 16, true), s, null);
            Equal(4096, laptop.ContextLength, "16 GB laptop: context halved to keep the KV cache to a quarter of the budget");
            Check(laptop.Prefetch, "laptop: prefetch on");
            Check(laptop.CacheBytes > 2 * GB && laptop.CacheBytes < s.StreamedBytes, "laptop: part of the model pinned, got " + laptop.CacheBytes);
            Equal(8, laptop.Threads, "laptop on battery: one thread per physical core");
            Check(laptop.Rules.Exists(delegate(string r) { return r.Contains("On battery"); }), "laptop: the battery rule is explained");

            Playbook small = Playbook.Plan(Machine(8, 3, 4, 8, false), s, null);
            Equal(Playbook.MinContext, small.ContextLength, "small: smallest context");
            Check(!small.Prefetch, "small: prefetch off");
            Equal(0L, small.CacheBytes, "small: nothing pinned");
            Check(small.Rules.Exists(delegate(string r) { return r.Contains("Very little free RAM"); }), "small: warns about RAM");
            foreach (Playbook p in new Playbook[] { desk, laptop, small }) Check(p.Rules.Count >= 5, "every choice has a reason");
        }

        static void PlaybookMeasured()
        {
            ModelShape s = Llama70B();
            Playbook m = new Playbook { ReadGBps = 5, LayerSecPhysical = 0.08, LayerSecLogical = 0.10 };
            Playbook p = Playbook.Plan(Machine(96, 80, 16, 32, false), s, m);
            Equal(16, p.Threads, "physical cores chosen when measured faster");
            Check(Math.Abs(p.PredictedSecPerToken - 80 * 0.08) < 1e-6, "all pinned: compute sets the pace, got " + p.PredictedSecPerToken);
            Playbook q = Playbook.Plan(Machine(32, 20, 8, 16, false), s, new Playbook { ReadGBps = 2, LayerSecPhysical = 0.1, LayerSecLogical = 0.1 });
            Equal(16, q.Threads, "no faster on physical cores: keep every logical core");
            Check(q.PredictedSecPerToken > 80 * 0.1, "laptop streaming: the disk is the limit, got " + q.PredictedSecPerToken);
            Check(q.Rules.Exists(delegate(string r) { return r.Contains("the disk is the limit"); }), "the limit is named");
        }

        static void PlaybookGuessAhead()
        {
            ModelShape s = Llama70B();
            Check(!Playbook.Plan(Machine(96, 80, 16, 32, false), s, null).Speculate, "unmeasured, whole model in RAM: off");
            Check(Playbook.Plan(Machine(32, 20, 8, 16, false), s, null).Speculate, "unmeasured, streaming: on (guesses share the read)");

            // Desktop, all pinned: a token costs 80 x 0.03 s; a batch of 9 at 0.12 s per layer is 4x that: off.
            Playbook costly = Playbook.Plan(Machine(96, 80, 16, 32, false), s, new Playbook { ReadGBps = 5, LayerSecPhysical = 0.035, LayerSecLogical = 0.03, LayerSecBatch = 0.12 });
            Check(!costly.Speculate, "batch 4x a token: off");
            Check(costly.Rules.Exists(delegate(string r) { return r.Contains("4.0x"); }), "the cost is explained");
            Playbook cheap = Playbook.Plan(Machine(96, 80, 16, 32, false), s, new Playbook { ReadGBps = 5, LayerSecPhysical = 0.035, LayerSecLogical = 0.03, LayerSecBatch = 0.06 });
            Check(cheap.Speculate, "batch 2x a token: on");

            // Real runs where guesses were rarely right turn it off, whatever the measurements say.
            Playbook seen = new Playbook { ReadGBps = 5, LayerSecPhysical = 0.035, LayerSecLogical = 0.03, LayerSecBatch = 0.06 };
            seen.Observe(100, 50, 300, 900, 400, 20);
            Playbook p = Playbook.Plan(Machine(96, 80, 16, 32, false), s, seen);
            Check(!p.Speculate, "5% of guesses kept: off");
            Check(p.Rules.Exists(delegate(string r) { return r.StartsWith("Recent runs: 3.00 s per reply token"); }), "real runs are reported");
        }

        static void PlaybookSavedAndApplied()
        {
            ModelShape s = Llama70B();
            MachineFacts m = Machine(96, 80, 16, 32, false);
            string path = Playbook.PathFor(_temp, "llama-70b");
            Playbook.Plan(m, s, new Playbook { ReadGBps = 5, LayerSecPhysical = 0.08, LayerSecLogical = 0.1 }).Save(path, m);
            Playbook back = Playbook.LoadHistory(path, m, s);
            Check(back != null && back.LayerSecPhysical == 0.08 && back.ReadGBps == 5, "measurements read back");
            Check(Playbook.LoadHistory(path, Machine(64, 50, 16, 32, false), s) == null, "another machine does not reuse them");
            ModelShape other = Llama70B(); other.Key = "llama-70b|2";
            Check(Playbook.LoadHistory(path, m, other) == null, "a changed model file does not reuse them");

            // The CLI applies the playbook, and options given by hand win.
            string model = Model(new TinySpec { Arch = "llama", Type = GgmlType.Q8_0 });
            EngineOptions auto = Program.Engine(Program.ParseArgs(new string[] { "run", "--model", model }, 1), _temp);
            Check(auto.Playbook != null && auto.ContextLength > 0 && auto.Threads > 0, "serve and run get a playbook");
            Check(File.Exists(Playbook.PathFor(_temp, LayeredBackend.DefaultId(model))), "the playbook is saved");
            EngineOptions hand = Program.Engine(Program.ParseArgs(new string[] { "run", "--model", model, "--threads", "3", "--no-prefetch", "--cache-gb", "0" }, 1), _temp);
            Equal(3, hand.Threads, "--threads wins");
            Check(!hand.Prefetch && hand.CacheBytes == 0, "--no-prefetch and --cache-gb win");
            EngineOptions off = Program.Engine(Program.ParseArgs(new string[] { "run", "--model", model, "--no-playbook" }, 1), _temp);
            Check(off.Playbook == null && off.Threads == 0 && off.Prefetch, "--no-playbook keeps the defaults");
        }

        static void CliJson()
        {
            TextWriter old = Console.Out;
            StringWriter sw = new StringWriter();
            string model = Model(new TinySpec { Arch = "llama", Type = GgmlType.Q8_0 });
            string outFile = Path.Combine(_temp, "playbook-out.json");
            int rc1, rc2, rc3, rc4;
            try
            {
                Console.SetOut(sw);
                rc1 = Program.Run(new string[] { "commands", "--json" }, _temp, null, "0");
                rc2 = Program.Run(new string[] { "playbook", "show", "--model", model, "--json" }, _temp, null, "0");
                rc3 = Program.Run(new string[] { "playbook", "show", "--model", model, "--out", outFile }, _temp, null, "0");
                rc4 = Program.Run(new string[] { "playbook", "probe", "--model", model, "--json" }, _temp, null, "0");
            }
            finally { Console.SetOut(old); }
            Equal(0, rc1, "commands exit code"); Equal(0, rc2, "playbook show exit code"); Equal(0, rc3, "playbook show --out exit code");
            Equal(0, rc4, "playbook probe exit code");
            string[] lines = sw.ToString().Split(new char[] { '\n' }, StringSplitOptions.RemoveEmptyEntries);
            Dictionary<string, object> cmds = Json.Parse(lines[0].Trim()) as Dictionary<string, object>;
            Equal("windlass.commands/1", Json.GetStr(cmds, "schema", ""), "commands schema");
            Check(Json.Write(cmds).Contains("playbook probe"), "commands lists playbook probe");
            Dictionary<string, object> pb = Json.Parse(lines[1].Trim()) as Dictionary<string, object>;
            Equal("windlass.playbook/1", Json.GetStr(pb, "schema", ""), "playbook schema");
            Check(pb.ContainsKey("settings") && pb.ContainsKey("rules") && pb.ContainsKey("machine"), "playbook has settings, rules and machine");
            byte[] raw = File.ReadAllBytes(outFile);
            Check(raw.Length > 3 && !(raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF), "--out writes no BOM");
            Check(Json.Parse(Encoding.UTF8.GetString(raw)) is Dictionary<string, object>, "--out writes valid JSON");
            Dictionary<string, object> probed = Json.Parse(lines[lines.Length - 1].Trim()) as Dictionary<string, object>;
            Dictionary<string, object> measured = probed["measured"] as Dictionary<string, object>;
            Check(Json.GetNum(measured, "readGBps", 0) > 0 && Json.GetNum(measured, "layerSecLogical", 0) > 0, "probe measures read speed and layer time");
            Check(Json.GetNum(((Dictionary<string, object>)probed["settings"]), "predictedSecPerToken", 0) > 0, "probe predicts seconds per token");
        }

        // ---------------- API ----------------

        const int TestPort = 18777;

        sealed class TestServer : IDisposable
        {
            public ApiServer Server;
            public Backend Backend;
            public TestServer(Backend b, ServerSettings s) : this(b, s, true) { }
            public TestServer(Backend b, ServerSettings s, bool waitReady)
            {
                Backend = b;
                s.Port = TestPort;
                Server = new ApiServer(b, s);
                Server.Start();
                for (int i = 0; i < 500 && waitReady && b.State == BackendState.Loading && s.LoadOnStartup; i++) Thread.Sleep(20);
            }
            public void Dispose() { Server.Dispose(); Backend.Dispose(); Thread.Sleep(50); }
        }

        static TestServer Stub(int delayMs, ServerSettings s)
        {
            StubBackend b = new StubBackend("stub-model");
            b.TokenDelayMs = delayMs;
            return new TestServer(b, s ?? new ServerSettings());
        }

        sealed class Resp
        {
            public int Status;
            public string Body;
            public WebHeaderCollection Headers;
            public Dictionary<string, object> Json { get { return Windlass.Json.Parse(Body) as Dictionary<string, object>; } }
        }

        static Resp Call(string method, string path, string body, params string[] headers)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + TestPort + path);
            req.Method = method;
            req.Proxy = null;
            req.Timeout = 20000;
            for (int i = 0; i < headers.Length; i += 2)
            {
                if (headers[i] == "Host") req.Host = headers[i + 1];
                else req.Headers[headers[i]] = headers[i + 1];
            }
            if (body != null)
            {
                byte[] b = Encoding.UTF8.GetBytes(body);
                req.ContentType = "application/json";
                req.ContentLength = b.Length;
                using (Stream s = req.GetRequestStream()) s.Write(b, 0, b.Length);
            }
            HttpWebResponse res;
            try { res = (HttpWebResponse)req.GetResponse(); }
            catch (WebException e)
            {
                res = e.Response as HttpWebResponse;
                if (res == null) throw;
            }
            using (res)
            using (StreamReader r = new StreamReader(res.GetResponseStream(), Encoding.UTF8))
                return new Resp { Status = (int)res.StatusCode, Body = r.ReadToEnd(), Headers = res.Headers };
        }

        static object Path_(Dictionary<string, object> o, params object[] keys)
        {
            object cur = o;
            foreach (object k in keys)
            {
                if (k is int) cur = ((List<object>)cur)[(int)k];
                else cur = ((Dictionary<string, object>)cur)[(string)k];
            }
            return cur;
        }

        static void ExpectError(Resp r, int status, string type)
        {
            Equal(status, r.Status, "status (" + r.Body + ")");
            Equal(type, (string)Path_(r.Json, "error", "type"), "error type");
            Check(((string)Path_(r.Json, "error", "message")).Length > 0, "error message");
        }

        const string Hi = "{\"model\":\"stub-model\",\"messages\":[{\"role\":\"user\",\"content\":\"hi there\"}]";

        static void ApiBasics()
        {
            using (Stub(0, null))
            {
                Resp m = Call("GET", "/v1/models", null);
                Equal(200, m.Status, "models status");
                Equal("list", (string)Path_(m.Json, "object"), "object");
                Equal("stub-model", (string)Path_(m.Json, "data", 0, "id"), "model id");
                Equal(4096.0, (double)Path_(m.Json, "data", 0, "context_length"), "context length");
                Equal(200, Call("GET", "/v1/models/stub-model", null).Status, "one model");
                ExpectError(Call("GET", "/v1/models/other", null), 404, "not_found_error");
                Resp h = Call("GET", "/health", null);
                Equal("ready", (string)Path_(h.Json, "status"), "health status");
                Equal(false, (bool)Path_(h.Json, "busy"), "not busy");
                Resp id = Call("GET", "/__fleethacker/id", null);
                Equal("windlass", (string)Path_(id.Json, "tool"), "fleethacker tool id");
                ExpectError(Call("GET", "/nope", null), 404, "not_found_error");
                ExpectError(Call("GET", "/v1/chat/completions", null), 405, "invalid_request_error");
            }
        }

        static void ApiPlain()
        {
            using (Stub(0, null))
            {
                Resp r = Call("POST", "/v1/chat/completions", Hi + "}");
                Equal(200, r.Status, "status");
                Equal("chat.completion", (string)Path_(r.Json, "object"), "object");
                Equal("Hello from Windlass, layer by layer.", (string)Path_(r.Json, "choices", 0, "message", "content"), "content");
                Equal("assistant", (string)Path_(r.Json, "choices", 0, "message", "role"), "role");
                Equal("stop", (string)Path_(r.Json, "choices", 0, "finish_reason"), "finish");
                Equal(2.0, (double)Path_(r.Json, "usage", "prompt_tokens"), "prompt tokens");
                Equal(6.0, (double)Path_(r.Json, "usage", "completion_tokens"), "completion tokens");
                Equal(8.0, (double)Path_(r.Json, "usage", "total_tokens"), "total tokens");
                Check(((string)Path_(r.Json, "id")).StartsWith("chatcmpl-"), "id");

                Resp len = Call("POST", "/v1/chat/completions", Hi + ",\"max_tokens\":2}");
                Equal("Hello from", (string)Path_(len.Json, "choices", 0, "message", "content"), "max_tokens");
                Equal("length", (string)Path_(len.Json, "choices", 0, "finish_reason"), "length");
                Resp stop = Call("POST", "/v1/chat/completions", Hi + ",\"stop\":[\"layer\"]}");
                Equal("Hello from Windlass, ", (string)Path_(stop.Json, "choices", 0, "message", "content"), "stop");
                Resp parts = Call("POST", "/v1/chat/completions", "{\"messages\":[{\"role\":\"user\",\"content\":[{\"type\":\"text\",\"text\":\"a b c\"}]}]}");
                Equal(3.0, (double)Path_(parts.Json, "usage", "prompt_tokens"), "content parts are read; model may be omitted");
            }
        }

        static List<string> ReadSse(Stream s, int maxEvents)
        {
            List<string> events = new List<string>();
            StreamReader r = new StreamReader(s, Encoding.UTF8);
            StringBuilder cur = new StringBuilder();
            string line;
            while (events.Count < maxEvents && (line = r.ReadLine()) != null)
            {
                if (line.Length == 0) { if (cur.Length > 0) { events.Add(cur.ToString()); cur.Clear(); } continue; }
                cur.Append(line);
            }
            return events;
        }

        static HttpWebResponse OpenStream(string body)
        {
            HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + TestPort + "/v1/chat/completions");
            req.Method = "POST";
            req.Proxy = null;
            req.Timeout = 20000;
            req.ReadWriteTimeout = 20000;
            req.ContentType = "application/json";
            byte[] b = Encoding.UTF8.GetBytes(body);
            using (Stream st = req.GetRequestStream()) st.Write(b, 0, b.Length);
            return (HttpWebResponse)req.GetResponse();
        }

        static void ApiStream()
        {
            using (Stub(0, null))
            {
                using (HttpWebResponse res = OpenStream(Hi + ",\"stream\":true,\"stream_options\":{\"include_usage\":true}}"))
                {
                    Check(res.ContentType.StartsWith("text/event-stream"), "content type " + res.ContentType);
                    List<string> ev = ReadSse(res.GetResponseStream(), 100);
                    Equal(": accepted", ev[0], "first comment");
                    Equal("data: [DONE]", ev[ev.Count - 1], "ends with [DONE]");
                    StringBuilder text = new StringBuilder();
                    string finish = null;
                    Dictionary<string, object> usage = null;
                    bool sawRole = false;
                    foreach (string e in ev)
                    {
                        if (!e.StartsWith("data: {")) continue;
                        Dictionary<string, object> c = (Dictionary<string, object>)Json.Parse(e.Substring(6));
                        Equal("chat.completion.chunk", (string)c["object"], "chunk object");
                        List<object> choices = (List<object>)c["choices"];
                        if (choices.Count == 0) { usage = (Dictionary<string, object>)c["usage"]; continue; }
                        Dictionary<string, object> delta = (Dictionary<string, object>)Path_(c, "choices", 0, "delta");
                        if (delta.ContainsKey("role")) sawRole = true;
                        if (delta.ContainsKey("content")) text.Append((string)delta["content"]);
                        if (Path_(c, "choices", 0, "finish_reason") != null) finish = (string)Path_(c, "choices", 0, "finish_reason");
                    }
                    Check(sawRole, "a role chunk");
                    Equal("Hello from Windlass, layer by layer.", text.ToString(), "streamed text");
                    Equal("stop", finish, "finish reason");
                    Check(usage != null && (double)usage["completion_tokens"] == 6, "usage chunk");
                }
                using (HttpWebResponse res = OpenStream(Hi + ",\"stream\":true}"))
                {
                    foreach (string e in ReadSse(res.GetResponseStream(), 100)) Check(!e.Contains("\"usage\""), "no usage chunk unless asked");
                }
            }
        }

        static void ApiErrors()
        {
            using (Stub(0, null))
            {
                ExpectError(Call("POST", "/v1/chat/completions", "{not json"), 400, "invalid_request_error");
                ExpectError(Call("POST", "/v1/chat/completions", "[1,2]"), 400, "invalid_request_error");
                ExpectError(Call("POST", "/v1/chat/completions", "{\"messages\":[]}"), 400, "invalid_request_error");
                ExpectError(Call("POST", "/v1/chat/completions", "{\"messages\":[{\"content\":\"x\"}]}"), 400, "invalid_request_error");
                ExpectError(Call("POST", "/v1/chat/completions", Hi + ",\"n\":2}"), 400, "invalid_request_error");
                ExpectError(Call("POST", "/v1/chat/completions", Hi + ",\"max_tokens\":0}"), 400, "invalid_request_error");
                ExpectError(Call("POST", "/v1/chat/completions", Hi + ",\"max_tokens\":1.5}"), 400, "invalid_request_error");
                ExpectError(Call("POST", "/v1/chat/completions", Hi + ",\"temperature\":3}"), 400, "invalid_request_error");
                ExpectError(Call("POST", "/v1/chat/completions", Hi + ",\"stop\":[1]}"), 400, "invalid_request_error");
                ExpectError(Call("POST", "/v1/chat/completions", "{\"model\":\"gpt-4\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}"), 404, "not_found_error");
            }
            StubBackend failing = new StubBackend("stub-model");
            failing.FailWith = new BackendException("prompt too long", 400, "context_length_exceeded");
            using (new TestServer(failing, new ServerSettings()))
                ExpectError(Call("POST", "/v1/chat/completions", Hi + "}"), 400, "context_length_exceeded");
        }

        static void ApiKey()
        {
            using (Stub(0, new ServerSettings { ApiKey = "s3cret" }))
            {
                ExpectError(Call("GET", "/v1/models", null), 401, "authentication_error");
                ExpectError(Call("POST", "/v1/chat/completions", Hi + "}", "Authorization", "Bearer wrong"), 401, "authentication_error");
                Equal(200, Call("GET", "/v1/models", null, "Authorization", "Bearer s3cret").Status, "bearer key");
                Equal(200, Call("POST", "/v1/chat/completions", Hi + "}", "X-Api-Key", "s3cret").Status, "x-api-key");
                Equal(200, Call("GET", "/health", null).Status, "health needs no key");
            }
        }

        static void ApiLoadStates()
        {
            StubBackend slow = new StubBackend("stub-model");
            slow.LoadDelayMs = 2000;
            using (new TestServer(slow, new ServerSettings(), false))
            {
                Resp r = Call("POST", "/v1/chat/completions", Hi + "}");
                ExpectError(r, 503, "model_loading");
                Equal("30", r.Headers["Retry-After"], "Retry-After");
                Equal("loading", (string)Path_(Call("GET", "/health", null).Json, "status"), "health while loading");
            }
            StubBackend broken = new StubBackend("stub-model");
            broken.State = BackendState.Error;
            broken.LoadError = "IOException: disk on fire";
            using (new TestServer(broken, new ServerSettings { LoadOnStartup = false }))
            {
                Resp r = Call("POST", "/v1/chat/completions", Hi + "}");
                ExpectError(r, 503, "model_load_failed");
                Check(r.Body.Contains("disk on fire"), "load error is reported");
            }
        }

        static void ApiEarlyHeadersAndKeepalive()
        {
            using (TestServer ts = Stub(400, new ServerSettings { KeepaliveMs = 150 }))
            {
                // The first request holds the turn for ~2.4 s; the second must get headers at once and keepalives while queued.
                Thread first = new Thread(delegate() { Call("POST", "/v1/chat/completions", Hi + "}"); });
                first.Start();
                Thread.Sleep(200);
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                using (HttpWebResponse res = OpenStream(Hi + ",\"stream\":true}"))
                {
                    Check(sw.ElapsedMilliseconds < 1500, "headers arrive while queued (" + sw.ElapsedMilliseconds + " ms)");
                    Equal(1, (int)(double)Path_(Call("GET", "/health", null).Json, "queued"), "one request queued");
                    List<string> ev = ReadSse(res.GetResponseStream(), 4);
                    Equal(": accepted", ev[0], "accepted");
                    Check(ev[2] == ": keepalive" || ev[3] == ": keepalive", "keepalive while queued: " + string.Join(" | ", ev.ToArray()));
                    res.Close();
                }
                first.Join();
            }
        }

        static void ApiQueueFull()
        {
            using (Stub(300, new ServerSettings { MaxQueue = 1 }))
            {
                Thread a = new Thread(delegate() { Call("POST", "/v1/chat/completions", Hi + "}"); });
                Thread b = new Thread(delegate() { Call("POST", "/v1/chat/completions", Hi + "}"); });
                a.Start(); Thread.Sleep(150); b.Start(); Thread.Sleep(150);
                Resp r = Call("POST", "/v1/chat/completions", Hi + "}");
                ExpectError(r, 503, "server_busy");
                Equal("60", r.Headers["Retry-After"], "Retry-After");
                a.Join(); b.Join();
                Equal(200, Call("POST", "/v1/chat/completions", Hi + ",\"max_tokens\":1}").Status, "free again afterwards");
            }
        }

        static void ApiCancelOnDisconnect()
        {
            StubBackend b = new StubBackend("stub-model");
            b.TokenDelayMs = 300;
            using (new TestServer(b, new ServerSettings { KeepaliveMs = 100 }))
            {
                // A raw socket, so the test can drop the connection mid-stream.
                using (TcpClient c = new TcpClient("127.0.0.1", TestPort))
                {
                    string body = Hi + ",\"stream\":true}";
                    byte[] req = Encoding.UTF8.GetBytes("POST /v1/chat/completions HTTP/1.1\r\nHost: 127.0.0.1:" + TestPort +
                        "\r\nContent-Type: application/json\r\nContent-Length: " + Encoding.UTF8.GetByteCount(body) + "\r\n\r\n" + body);
                    NetworkStream ns = c.GetStream();
                    ns.Write(req, 0, req.Length);
                    byte[] buf = new byte[4096];
                    int got = 0;
                    System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                    while (sw.ElapsedMilliseconds < 5000)
                    {
                        got += ns.Read(buf, got, buf.Length - got);
                        if (Encoding.UTF8.GetString(buf, 0, got).Contains("Hello")) break;
                    }
                    Check(Encoding.UTF8.GetString(buf, 0, got).Contains("Hello"), "got the first word");
                    c.Client.LingerState = new LingerOption(true, 0);
                }
                Check(b.Cancelled.WaitOne(5000), "the generation was cancelled after the client left");
                Thread.Sleep(200);
                Equal(false, (bool)Path_(Call("GET", "/health", null).Json, "busy"), "the turn was released");
            }
        }

        static void ApiHostOrigin()
        {
            using (Stub(0, null))
            {
                // HttpListener itself may answer 400 (no matching prefix) before Windlass's own 403.
                int forged = Call("GET", "/v1/models", null, "Host", "evil.example:" + TestPort).Status;
                Check(forged == 403 || forged == 400, "a foreign Host is refused (" + forged + ")");
                Equal(200, Call("GET", "/v1/models", null, "Host", "localhost:" + TestPort).Status, "localhost host");
                ExpectError(Call("GET", "/v1/models", null, "Origin", "https://evil.example"), 403, "forbidden");
                Resp ok = Call("GET", "/v1/models", null, "Origin", "http://127.0.0.1:8767");
                Equal(200, ok.Status, "a loopback page may call the API");
                Equal("http://127.0.0.1:8767", ok.Headers["Access-Control-Allow-Origin"], "CORS for loopback");
                ExpectError(Call("GET", "/__fleethacker/id", null, "Origin", "http://127.0.0.1:8767"), 403, "forbidden");
                ExpectError(Call("POST", "/__fleethacker/shutdown", "", "Origin", "http://localhost:9999"), 403, "forbidden");
            }
        }

        static void ApiRealModel()
        {
            EngineOptions o = new EngineOptions { ModelPath = Model(new TinySpec { Arch = "qwen3", Type = GgmlType.Q4_K }), ModelId = "tiny" };
            using (TestServer ts = new TestServer(new LayeredBackend(o), new ServerSettings()))
            {
                Equal(512, (int)(double)Path_(Call("GET", "/v1/models", null).Json, "data", 0, "context_length"), "context from the model");
                Resp r = Call("POST", "/v1/chat/completions", "{\"model\":\"tiny\",\"messages\":[{\"role\":\"user\",\"content\":\"Hello world\"}],\"max_tokens\":5,\"temperature\":0}");
                Equal(200, r.Status, "status (" + r.Body + ")");
                Equal(5.0, (double)Path_(r.Json, "usage", "completion_tokens"), "five tokens");
                Equal("length", (string)Path_(r.Json, "choices", 0, "finish_reason"), "length");
                Resp sh = Call("POST", "/__fleethacker/shutdown", "", "X-FleetHacker-Shutdown", "1");
                Equal(200, sh.Status, "shutdown accepted");
                Check(ts.Server.Stopped.WaitOne(3000), "server stops after /__fleethacker/shutdown");
            }
        }
    }
}

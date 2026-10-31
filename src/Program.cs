// Command line. C# 5 only. Called by Start-Windlass.ps1 after it compiles src\*.cs with Add-Type.
//   serve  run the OpenAI-compatible server
//   run    generate one reply in the console (no server)
//   info   print a model's shape and what streaming it will need
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace Windlass
{
    public static class Program
    {
        public const string Usage =
@"Windlass: run very large GGUF models one layer at a time, behind an OpenAI-compatible API.

  Launch-Windlass.bat serve --model <file.gguf> [options]   run the server on http://127.0.0.1:8777/v1
  Launch-Windlass.bat run   --model <file.gguf> --prompt <text> [options]   one reply in the console
  Launch-Windlass.bat info  --model <file.gguf>   model shape, sizes and RAM needed
  Launch-Windlass.bat playbook probe --model <file.gguf> [--json] [--out <file>]
                          measure this computer and write its playbook for the model (also: probe)
  Launch-Windlass.bat playbook show  --model <file.gguf> [--json] [--out <file>]
                          the settings serve and run would use now, and why (measures nothing)
  Launch-Windlass.bat playbook eval  --model <file.gguf> [--tokens <n>] [--json] [--out <file>]
                          score quality: perplexity on a built-in passage (lower is better), and prompt speed
  Launch-Windlass.bat commands [--json]   every command with its inputs and outputs

serve and run pick their settings from this computer's playbook (RAM free, cores, battery; plus measurements after
playbook probe). Any option below that you give yourself overrides the playbook's choice. --json prints a versioned
JSON document instead of text; --out writes it to a file as UTF-8 without a BOM. Exit code 0 is success, 1 an error,
2 a usage error.

Options:
  --model <path>          a .gguf file (for a split model, any one of its -0000N-of-0000M.gguf parts)
  --model-id <id>         id published in /v1/models (default: the file name)
  --port <n>              port (default: the first free port in 8777-8779)
  --api-key <key>         require 'Authorization: Bearer <key>' (default: $env:WINDLASS_API_KEY)
  --context-length <n>    context to allow (default: the model's, at most 8192)
  --cache-gb <n>          keep up to n GB of layers resident in RAM instead of re-reading them (default 0)
  --no-prefetch           do not read the next layer while the current one computes
  --threads <n>           worker threads for the math (default: every logical core, or the playbook's choice)
  --int-dot               integer dot products with 8-bit activations, llama.cpp's CPU method (default off; logits move slightly)
  --no-playbook           ignore the playbook; use the defaults and the options given
  --no-auto-stop          do not stop when the programs that sent heartbeats are gone (or set WINDLASS_AUTO_STOP=0)
  --guess / --no-guess    guess-ahead for greedy replies on or off (default: the playbook's choice, else off)
  --chat-template <name>  llama3, chatml, mistral, mistral-v7 or llama2 (default: detected)
  --max-tokens <n>        max_tokens for requests that give none (default 1024)
  --max-queue <n>         requests that may wait behind the running one (default 8)
  --keepalive <s>         seconds between SSE keepalive comments, under 60 (default 15)
  --stub                  serve a canned reply with no model, to test wiring
  --temperature <t>       (run) sampling temperature, 0 = greedy (default 0)
  --verbose               log every layer
";

        public static int Main(string[] args)
        {
            return Run(args, AppDomain.CurrentDomain.BaseDirectory, null, "0.0.0");
        }

        public static int Run(string[] args, string root, string scriptPath, string version)
        {
            Thread.CurrentThread.CurrentCulture = CultureInfo.InvariantCulture;
            try { Console.OutputEncoding = new UTF8Encoding(false); } catch (IOException) { }   // no console (hidden start)
            if (args.Length == 0 || args[0] == "help" || args[0] == "--help" || args[0] == "-h") { Console.WriteLine(Usage); return args.Length == 0 ? 2 : 0; }
            if (args[0] == "--version") { Console.WriteLine("windlass " + version); return 0; }
            // Two-word commands (<noun> <verb>, as in the Fleet CLI parity proposal) take their options after the verb.
            string command = args[0];
            int optionsFrom = 1;
            if (command == "playbook")
            {
                if (args.Length < 2 || args[1].StartsWith("--")) { Console.Error.WriteLine("playbook needs a verb: probe or show. Run Launch-Windlass.bat help."); return 2; }
                command = "playbook " + args[1];
                optionsFrom = 2;
            }
            else if (command == "probe") command = "playbook probe";
            Dictionary<string, string> o;
            try { o = ParseArgs(args, optionsFrom); }
            catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
            Log.Verbose = o.ContainsKey("verbose");
            try
            {
                switch (command)
                {
                    case "serve": return Serve(o, root, scriptPath, version);
                    case "run": return RunOnce(o, root);
                    case "info": return Info(o);
                    case "playbook probe": return PlaybookCommand(o, root, true);
                    case "playbook show": return PlaybookCommand(o, root, false);
                    case "playbook eval": return PlaybookEval(o, root);
                    case "commands": return Commands(o);
                }
                Console.Error.WriteLine("Unknown command '" + command + "'.\n\n" + Usage);
                return 2;
            }
            catch (Exception e)
            {
                Log.Error(e is NotSupportedException || e is ArgumentException || e is FileNotFoundException || e is InvalidDataException || e is BackendException ? e.Message : e.ToString());
                return 1;
            }
        }

        static readonly string[] Flags = { "int-dot", "no-auto-stop", "no-prefetch", "no-playbook", "stub", "verbose", "json", "guess", "no-guess" };

        public static Dictionary<string, string> ParseArgs(string[] args, int from)
        {
            Dictionary<string, string> o = new Dictionary<string, string>();
            for (int i = from; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("--")) throw new ArgumentException("Unexpected argument '" + a + "'. Run Launch-Windlass.bat help.");
                string key = a.Substring(2), val = null;
                int eq = key.IndexOf('=');
                if (eq >= 0) { val = key.Substring(eq + 1); key = key.Substring(0, eq); }
                else if (Array.IndexOf(Flags, key) < 0)
                {
                    if (i + 1 >= args.Length) throw new ArgumentException("--" + key + " needs a value.");
                    val = args[++i];
                }
                o[key] = val ?? "1";
            }
            return o;
        }

        static string Need(Dictionary<string, string> o, string key)
        {
            string v;
            if (!o.TryGetValue(key, out v) || string.IsNullOrEmpty(v)) throw new ArgumentException("--" + key + " is required. Run Launch-Windlass.bat help.");
            return v;
        }

        static int Int(Dictionary<string, string> o, string key, int dflt, int min, int max)
        {
            string v;
            if (!o.TryGetValue(key, out v)) return dflt;
            int n;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) || n < min || n > max)
                throw new ArgumentException("--" + key + " must be a whole number from " + min + " to " + max + ".");
            return n;
        }

        static double Dbl(Dictionary<string, string> o, string key, double dflt, double min, double max)
        {
            string v;
            if (!o.TryGetValue(key, out v)) return dflt;
            double n;
            if (!double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out n) || n < min || n > max)
                throw new ArgumentException("--" + key + " must be a number from " + min + " to " + max + ".");
            return n;
        }

        // Settings for the engine: this computer's playbook first (root = the Windlass folder, null = no playbook),
        // then every option the user gave, which wins.
        public static EngineOptions Engine(Dictionary<string, string> o, string root)
        {
            EngineOptions e = new EngineOptions();
            e.ModelPath = Path.GetFullPath(Need(o, "model"));
            if (!File.Exists(e.ModelPath)) throw new FileNotFoundException("No model file at " + e.ModelPath, e.ModelPath);
            string v;
            if (o.TryGetValue("model-id", out v)) e.ModelId = v;
            if (o.TryGetValue("chat-template", out v)) e.ChatTemplate = v;
            if (root != null && !o.ContainsKey("no-playbook")) ApplyPlaybook(e, root);
            if (o.ContainsKey("no-prefetch")) e.Prefetch = false;
            if (o.ContainsKey("context-length")) e.ContextLength = Int(o, "context-length", 0, 16, 1 << 20);
            if (o.ContainsKey("cache-gb")) e.CacheBytes = (long)(Dbl(o, "cache-gb", 0, 0, 1 << 20) * 1e9);
            if (o.ContainsKey("threads")) e.Threads = Int(o, "threads", 0, 1, 4096);
            if (o.ContainsKey("guess")) e.Speculate = true;
            if (o.ContainsKey("no-guess")) e.Speculate = false;
            if (o.ContainsKey("int-dot")) e.IntDot = true;
            if (e.Playbook != null && (o.ContainsKey("no-prefetch") || o.ContainsKey("context-length") || o.ContainsKey("cache-gb") || o.ContainsKey("threads") || o.ContainsKey("guess") || o.ContainsKey("no-guess")))
                e.Playbook.Rules.Add("Options given on the command line override the playbook where they differ.");
            return e;
        }

        static void ApplyPlaybook(EngineOptions e, string root)
        {
            GgufModelFile g = GgufModelFile.Open(e.ModelPath);
            ModelConfig c = ModelConfig.From(g);
            ModelShape s = ModelShape.From(g, c, e.ModelId ?? LayeredBackend.DefaultId(e.ModelPath));
            MachineFacts m = MachineFacts.Detect();
            string path = Playbook.PathFor(root, s.Id);
            Playbook p = Playbook.Plan(m, s, Playbook.LoadHistory(path, m, s));
            try { p.Save(path, m); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            e.CacheBytes = p.CacheBytes;
            e.Prefetch = p.Prefetch;
            e.ContextLength = p.ContextLength;
            e.Threads = p.Threads;
            e.Speculate = p.Speculate;
            e.Playbook = p;
            e.PlaybookPath = path;
            e.Machine = m;
        }

        // playbook probe: reads a few layers and times one layer's math with physical and with logical cores, then
        // writes the playbook that serve and run will use on this computer.
        // playbook show: the settings serve and run would pick right now (free RAM changes), using saved measurements.
        static int PlaybookCommand(Dictionary<string, string> o, string root, bool measure)
        {
            bool json = o.ContainsKey("json");
            string path = Path.GetFullPath(Need(o, "model"));
            if (!File.Exists(path)) throw new FileNotFoundException("No model file at " + path, path);
            GgufModelFile g = GgufModelFile.Open(path);
            ModelConfig c = ModelConfig.From(g);
            string id;
            if (!o.TryGetValue("model-id", out id)) id = LayeredBackend.DefaultId(path);
            ModelShape s = ModelShape.From(g, c, id);
            MachineFacts m = MachineFacts.Detect();
            string file = Playbook.PathFor(root, id);
            if (measure && !json) Console.WriteLine("Measuring: reading a few layers and timing one layer's math...");
            Playbook history = Playbook.LoadHistory(file, m, s), measured = null;
            if (measure)
            {
                measured = Measure(g, c, m);
                if (history != null) { measured.QualityPerplexity = history.QualityPerplexity; measured.QualityTokens = history.QualityTokens; measured.PromptSecPerToken = history.PromptSecPerToken; }
            }
            Playbook p = Playbook.Plan(m, s, measure ? measured : history);
            p.Save(file, m);

            string outPath;
            if (json || o.TryGetValue("out", out outPath))
            {
                string doc = p.ToJson(m);
                if (o.TryGetValue("out", out outPath)) File.WriteAllText(Path.GetFullPath(outPath), doc, new UTF8Encoding(false));
                else Console.WriteLine(doc);
                if (!json) Console.WriteLine("Wrote " + Path.GetFullPath(outPath));
                return 0;
            }
            Console.WriteLine("computer        " + m.Cpu + ", " + m.PhysicalCores + " cores / " + m.LogicalCores + " threads");
            Console.WriteLine("RAM             " + (m.TotalRam / 1e9).ToString("F1") + " GB total, " + (m.AvailableRam / 1e9).ToString("F1") + " GB free");
            Console.WriteLine("power           " + (m.HasBattery ? (m.OnBattery ? "on battery" : "plugged in (has a battery)") : "mains (no battery)"));
            if (m.Gpus.Count > 0) Console.WriteLine("graphics        " + string.Join(", ", m.Gpus.ToArray()) + " (not used yet: Windlass computes on the CPU)");
            Console.WriteLine("model           " + id + ": " + c.Layers + " layers, " + (s.StreamedBytes / 1e9).ToString("F1") + " GB to stream");
            if (p.Measured)
            {
                Console.WriteLine("read speed      " + p.ReadGBps.ToString("F1") + " GB/s (from Windows' file cache if the model was read recently)");
                Console.WriteLine("one layer       " + p.LayerSecPhysical.ToString("F3") + " s on " + m.PhysicalCores + " threads, " + p.LayerSecLogical.ToString("F3") + " s on " + m.LogicalCores +
                    (p.LayerSecBatch > 0 ? "; " + p.LayerSecBatch.ToString("F3") + " s for " + (LayeredBackend.MaxGuesses + 1) + " tokens at once" : ""));
            }
            else Console.WriteLine("measured        not yet: run Launch-Windlass.bat playbook probe --model <file>");
            Console.WriteLine();
            Console.WriteLine("Playbook (" + file + "):");
            Console.WriteLine("  context " + p.ContextLength + ", prefetch " + (p.Prefetch ? "on" : "off") + ", pinned " + (p.CacheBytes / 1e9).ToString("F1") + " GB, " + p.Threads + " threads, guess-ahead " + (p.Speculate ? "on" : "off"));
            foreach (string r in p.Rules) Console.WriteLine("  - " + r);
            return 0;
        }

        // playbook eval: loads the model with this computer's settings, scores it on the built-in passage and records
        // the score in the playbook, where playbook show reports it next to the speed.
        static int PlaybookEval(Dictionary<string, string> o, string root)
        {
            bool json = o.ContainsKey("json");
            EngineOptions e = Engine(o, root);
            e.PlaybookPath = null;   // the eval pass is not a real run; recorded below as quality instead
            int tokens = Int(o, "tokens", Quality.DefaultTokens, 2, 1 << 20);
            LayeredBackend b = new LayeredBackend(e);
            QualityResult q;
            try
            {
                if (!json) Console.WriteLine("Loading " + b.ModelId + " and scoring it on " + tokens + " tokens of the built-in passage...");
                b.Load();
                q = b.Evaluate(tokens, CancellationToken.None);
            }
            finally { b.Dispose(); }
            Playbook p = e.Playbook;
            string file = p != null ? Playbook.PathFor(root, b.ModelId) : null;
            if (p != null)
            {
                // Re-plan so the quality rule is in the saved rules; free RAM is read again.
                p.SetQuality(q);
                GgufModelFile g = GgufModelFile.Open(e.ModelPath);
                ModelShape s = ModelShape.From(g, ModelConfig.From(g), b.ModelId);
                p = Playbook.Plan(e.Machine, s, p);
                p.Save(file, e.Machine);
            }
            string outPath;
            if (json || o.TryGetValue("out", out outPath))
            {
                Dictionary<string, object> r = new Dictionary<string, object>();
                r["schema"] = "windlass.quality/1"; r["model"] = b.ModelId; r["perplexity"] = q.Perplexity; r["tokens"] = (long)q.Tokens;
                r["seconds"] = q.Seconds; r["promptSecPerToken"] = q.SecPerPromptToken; r["passage"] = "Windlass test passage v1 (original, built in)";
                string doc = Json.Write(r);
                if (o.TryGetValue("out", out outPath)) File.WriteAllText(Path.GetFullPath(outPath), doc, new UTF8Encoding(false));
                else Console.WriteLine(doc);
                return 0;
            }
            Console.WriteLine("perplexity      " + q.Perplexity.ToString("F2") + " on " + q.Tokens + " tokens (lower is better; compare models with the same tokenizer)");
            Console.WriteLine("prompt speed    " + q.SecPerPromptToken.ToString("F3") + " s per token (" + q.Seconds.ToString("F1") + " s in all)");
            if (file != null) Console.WriteLine("recorded in     " + file);
            return 0;
        }

        // commands: the CLI describes itself, so an agent can find every command and its inputs and outputs.
        static readonly string[][] CommandList =
        {
            new[] { "serve", "--model [--model-id --port --api-key --context-length --cache-gb --no-prefetch --threads --int-dot --guess --no-guess --no-playbook --chat-template --max-tokens --max-queue --keepalive --no-auto-stop --stub --verbose]", "an OpenAI-compatible server on 127.0.0.1 (see docs/API.md)" },
            new[] { "run", "--model --prompt [--system --max-tokens --temperature and the serve options above]", "one reply in the console" },
            new[] { "info", "--model", "the model's shape, sizes and RAM needs (text)" },
            new[] { "playbook probe", "--model [--model-id --json --out]", "windlass.playbook/1: machine, measurements, settings, rules; saved in playbooks\\" },
            new[] { "playbook show", "--model [--model-id --json --out]", "windlass.playbook/1 for the settings serve and run would use now" },
            new[] { "playbook eval", "--model [--model-id --tokens --json --out]", "windlass.playbook/1 with quality: perplexity on the built-in passage (lower is better) and prompt seconds per token" },
            new[] { "commands", "[--json]", "windlass.commands/1: this list" },
        };

        static int Commands(Dictionary<string, string> o)
        {
            if (!o.ContainsKey("json"))
            {
                foreach (string[] c in CommandList) Console.WriteLine(c[0].PadRight(16) + c[1] + "\n" + "".PadRight(16) + "-> " + c[2]);
                return 0;
            }
            List<object> list = new List<object>();
            foreach (string[] c in CommandList)
            {
                Dictionary<string, object> d = new Dictionary<string, object>();
                d["name"] = c[0]; d["inputs"] = c[1]; d["output"] = c[2];
                list.Add(d);
            }
            Dictionary<string, object> doc = new Dictionary<string, object>();
            doc["schema"] = "windlass.commands/1";
            doc["commands"] = list;
            Console.WriteLine(Json.Write(doc));
            return 0;
        }

        static unsafe Playbook Measure(GgufModelFile g, ModelConfig c, MachineFacts m)
        {
            Playbook r = new Playbook();
            int saved = Transformer.Threads;
            try
            {
                using (LayerStream st = new LayerStream(g, c, false, 0))
                {
                    List<int> picks = new List<int>();
                    foreach (int i in new int[] { c.Layers / 4, c.Layers / 2, 3 * c.Layers / 4 }) if (!picks.Contains(i)) picks.Add(i);
                    long before = st.BytesRead;
                    Stopwatch sw = Stopwatch.StartNew();
                    Stage last = null;
                    foreach (int i in picks) last = st.Acquire(i);
                    double secs = Math.Max(sw.Elapsed.TotalSeconds, 1e-6);
                    r.ReadGBps = (st.BytesRead - before) / 1e9 / secs;
                    r.LayerSecLogical = TimeLayer(last, m.LogicalCores, 1);
                    r.LayerSecPhysical = m.PhysicalCores < m.LogicalCores ? TimeLayer(last, m.PhysicalCores, 1) : r.LayerSecLogical;
                    // A token plus a full set of guesses in one pass, as guess-ahead checks them.
                    r.LayerSecBatch = TimeLayer(last, r.LayerSecPhysical < r.LayerSecLogical * 0.97 ? m.PhysicalCores : m.LogicalCores, LayeredBackend.MaxGuesses + 1);
                }
            }
            finally { Transformer.Threads = saved; }
            return r;
        }

        // Best of three: every matrix of one layer times T tokens (1 = generating one token).
        static double TimeLayer(Stage s, int threads, int T)
        {
            Transformer.Threads = threads;
            List<WeightView> mats = new List<WeightView>();
            foreach (WeightView w in s.Views.Values) if (w.Rows > 1) mats.Add(w);
            Random rng = new Random(1);
            float[][] xs = new float[mats.Count][], ys = new float[mats.Count][];
            for (int i = 0; i < mats.Count; i++)
            {
                xs[i] = new float[mats[i].Cols * T]; ys[i] = new float[mats[i].Rows * T];
                for (int j = 0; j < xs[i].Length; j++) xs[i][j] = (float)(rng.NextDouble() - 0.5);
            }
            double best = double.MaxValue;
            for (int rep = 0; rep < 3; rep++)
            {
                Stopwatch sw = Stopwatch.StartNew();
                for (int i = 0; i < mats.Count; i++) Transformer.MatMul(mats[i], xs[i], T, ys[i]);
                best = Math.Min(best, sw.Elapsed.TotalSeconds);
            }
            return best;
        }

        static int Serve(Dictionary<string, string> o, string root, string scriptPath, string version)
        {
            ServerSettings s = new ServerSettings();
            s.Port = Int(o, "port", 8777, 1, 65535);
            string key;
            s.ApiKey = o.TryGetValue("api-key", out key) ? key : Environment.GetEnvironmentVariable("WINDLASS_API_KEY");
            s.KeepaliveMs = (int)(Dbl(o, "keepalive", 15, 1, 59.999) * 1000);
            s.MaxQueue = Int(o, "max-queue", 8, 0, 1000);
            s.DefaultMaxTokens = Int(o, "max-tokens", 1024, 1, 1 << 20);
            s.Version = version;
            s.ScriptPath = scriptPath;
            s.AutoStop = !o.ContainsKey("no-auto-stop") && Environment.GetEnvironmentVariable("WINDLASS_AUTO_STOP") != "0";

            Backend backend;
            if (o.ContainsKey("stub"))
            {
                string id;
                StubBackend stub = new StubBackend(o.TryGetValue("model-id", out id) ? id : "windlass-stub");
                stub.TokenDelayMs = 200;
                backend = stub;
            }
            else backend = new LayeredBackend(Engine(o, root));

            string logs = Path.Combine(root, "logs");
            Log.ToFile(Path.Combine(logs, "Windlass_" + DateTime.Now.ToString("yyyy_MM_dd_HHmmss") + ".log"));
            int pid = Process.GetCurrentProcess().Id;
            string runtimeFile = Path.Combine(logs, "windlass-server-" + pid + ".json");
            using (ApiServer server = new ApiServer(backend, s))
            {
                server.Start();
                Dictionary<string, object> rt = new Dictionary<string, object>();
                rt["pid"] = pid; rt["port"] = s.Port; rt["url"] = "http://127.0.0.1:" + s.Port + "/v1";
                rt["started"] = DateTime.UtcNow.ToString("o");
                try { File.WriteAllText(runtimeFile, Json.Write(rt)); } catch (IOException) { }
                Log.Info("Windlass " + version + " on http://127.0.0.1:" + s.Port + "/v1 (Ctrl+C or Stop-Windlass.ps1 to stop)");
                if (string.IsNullOrEmpty(s.ApiKey)) Log.Info("No API key set: any program on this PC can use the model.");
                Console.CancelKeyPress += delegate(object sender, ConsoleCancelEventArgs e) { e.Cancel = true; Log.Info("stopping"); server.Stop(); };
                server.Stopped.WaitOne();
            }
            backend.Dispose();
            try { File.Delete(runtimeFile); } catch (IOException) { }
            Log.Info("stopped");
            return 0;
        }

        static int RunOnce(Dictionary<string, string> o, string root)
        {
            LayeredBackend b = new LayeredBackend(Engine(o, root));
            b.Load();
            GenerationRequest r = new GenerationRequest();
            string sys;
            if (o.TryGetValue("system", out sys)) r.Messages.Add(new ChatMessage("system", sys));
            r.Messages.Add(new ChatMessage("user", Need(o, "prompt")));
            r.MaxTokens = Int(o, "max-tokens", 256, 1, 1 << 20);
            r.Temperature = Dbl(o, "temperature", 0, 0, 2);
            Stopwatch sw = Stopwatch.StartNew();
            GenerationResult res = b.Generate(r, delegate(string t) { Console.Write(t); }, null, CancellationToken.None);
            Console.WriteLine();
            Log.Info(string.Format("{0}: {1} prompt + {2} completion tokens in {3:F1}s, {4:F1} GB read from disk",
                res.FinishReason, res.PromptTokens, res.CompletionTokens, sw.Elapsed.TotalSeconds, b.Model.Stream.BytesRead / 1e9));
            b.Dispose();
            return 0;
        }

        static int Info(Dictionary<string, string> o)
        {
            GgufModelFile g = GgufModelFile.Open(Need(o, "model"));
            ModelConfig c = ModelConfig.From(g);
            Tokenizer tok = new Tokenizer(g);
            ChatTemplate tpl = ChatTemplate.Detect(g, tok, null);
            Dictionary<string, long> types = new Dictionary<string, long>();
            foreach (GgufTensor t in g.Tensors)
            {
                string n = GgmlTypes.Name(t.Type);
                long v; types.TryGetValue(n, out v); types[n] = v + t.SizeBytes;
            }
            long layer = 0;
            foreach (GgufTensor t in g.Tensors) if (t.Name.StartsWith("blk.0.")) layer += t.SizeBytes;
            Console.WriteLine("name            " + c.Name);
            Console.WriteLine("architecture    " + c.Arch + (c.QkNorm ? " (QK norm)" : "") + (c.TiedEmbeddings ? ", tied embeddings" : ""));
            Console.WriteLine("layers          " + c.Layers + " x " + (layer / 1e9).ToString("F2") + " GB");
            Console.WriteLine("weights         " + (g.TotalBytes / 1e9).ToString("F2") + " GB in " + g.Paths.Count + " file(s)");
            Console.Write("types          ");
            foreach (KeyValuePair<string, long> kv in types) Console.Write(" " + kv.Key + " " + (kv.Value / 1e9).ToString("F2") + " GB;");
            Console.WriteLine();
            Console.WriteLine("shape           embd " + c.Embd + ", heads " + c.Heads + "/" + c.KvHeads + " kv, head dim " + c.HeadDim + ", vocab " + c.Vocab);
            Console.WriteLine("context         " + c.ContextLength + " (KV cache " + (2.0 * c.Layers * c.KvDim * 2 / 1e6).ToString("F2") + " MB per token)");
            Console.WriteLine("tokenizer       " + (tok.Spm ? "SentencePiece" : "byte-level BPE") + ", chat template " + tpl.Name);
            foreach (string w in c.Warnings) Console.WriteLine("warning         " + w);
            return 0;
        }
    }
}

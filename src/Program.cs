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

  Windlass.bat serve --model <file.gguf> [options]   run the server on http://127.0.0.1:8777/v1
  Windlass.bat run   --model <file.gguf> --prompt <text> [options]   one reply in the console
  Windlass.bat info  --model <file.gguf>   model shape, sizes and RAM needed

Options:
  --model <path>          a .gguf file (for a split model, any one of its -0000N-of-0000M.gguf parts)
  --model-id <id>         id published in /v1/models (default: the file name)
  --port <n>              port (default: the first free port in 8777-8779)
  --api-key <key>         require 'Authorization: Bearer <key>' (default: $env:WINDLASS_API_KEY)
  --context-length <n>    context to allow (default: the model's, at most 8192)
  --cache-gb <n>          keep up to n GB of layers resident in RAM instead of re-reading them (default 0)
  --no-prefetch           do not read the next layer while the current one computes
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
            Dictionary<string, string> o;
            try { o = ParseArgs(args, 1); }
            catch (ArgumentException e) { Console.Error.WriteLine(e.Message); return 2; }
            Log.Verbose = o.ContainsKey("verbose");
            try
            {
                switch (args[0])
                {
                    case "serve": return Serve(o, root, scriptPath, version);
                    case "run": return RunOnce(o);
                    case "info": return Info(o);
                }
                Console.Error.WriteLine("Unknown command '" + args[0] + "'.\n\n" + Usage);
                return 2;
            }
            catch (Exception e)
            {
                Log.Error(e is NotSupportedException || e is ArgumentException || e is FileNotFoundException || e is InvalidDataException || e is BackendException ? e.Message : e.ToString());
                return 1;
            }
        }

        static readonly string[] Flags = { "no-prefetch", "stub", "verbose" };

        public static Dictionary<string, string> ParseArgs(string[] args, int from)
        {
            Dictionary<string, string> o = new Dictionary<string, string>();
            for (int i = from; i < args.Length; i++)
            {
                string a = args[i];
                if (!a.StartsWith("--")) throw new ArgumentException("Unexpected argument '" + a + "'. Run Windlass.bat help.");
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
            if (!o.TryGetValue(key, out v) || string.IsNullOrEmpty(v)) throw new ArgumentException("--" + key + " is required. Run Windlass.bat help.");
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

        public static EngineOptions Engine(Dictionary<string, string> o)
        {
            EngineOptions e = new EngineOptions();
            e.ModelPath = Path.GetFullPath(Need(o, "model"));
            if (!File.Exists(e.ModelPath)) throw new FileNotFoundException("No model file at " + e.ModelPath, e.ModelPath);
            string v;
            if (o.TryGetValue("model-id", out v)) e.ModelId = v;
            if (o.TryGetValue("chat-template", out v)) e.ChatTemplate = v;
            e.Prefetch = !o.ContainsKey("no-prefetch");
            e.ContextLength = Int(o, "context-length", 0, 16, 1 << 20);
            e.CacheBytes = (long)(Dbl(o, "cache-gb", 0, 0, 1 << 20) * 1e9);
            return e;
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

            Backend backend;
            if (o.ContainsKey("stub"))
            {
                string id;
                StubBackend stub = new StubBackend(o.TryGetValue("model-id", out id) ? id : "windlass-stub");
                stub.TokenDelayMs = 200;
                backend = stub;
            }
            else backend = new LayeredBackend(Engine(o));

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

        static int RunOnce(Dictionary<string, string> o)
        {
            LayeredBackend b = new LayeredBackend(Engine(o));
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
            Console.WriteLine("context         " + c.ContextLength + " (KV cache " + (2.0 * c.Layers * c.KvDim * 4 / 1e6).ToString("F2") + " MB per token)");
            Console.WriteLine("tokenizer       " + (tok.Spm ? "SentencePiece" : "byte-level BPE") + ", chat template " + tpl.Name);
            foreach (string w in c.Warnings) Console.WriteLine("warning         " + w);
            return 0;
        }
    }
}

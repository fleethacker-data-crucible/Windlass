// LayeredBackend: GGUF model + tokenizer + chat template + layer-streamed forward pass + sampling. C# 5 only.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace Windlass
{
    public sealed class EngineOptions
    {
        public string ModelPath;
        public string ModelId;
        public bool Prefetch = true;
        public long CacheBytes;              // keep this many bytes of layers resident (0 = stream every layer)
        public int ContextLength;            // 0 = min(model's, DefaultContext)
        public string ChatTemplate;          // null = detect
        public int Threads;                  // 0 = every logical core
        public Playbook Playbook;            // the settings' reasons, logged at load (null = set by hand)
        public const int DefaultContext = 8192;
    }

    public sealed class LayeredBackend : Backend
    {
        readonly EngineOptions _o;
        GgufModelFile _g;
        public ModelConfig Config;
        public Tokenizer Tokenizer;
        public ChatTemplate Template;
        public Transformer Model;
        LayerStream _stream;
        readonly List<int> _cached = new List<int>();   // tokens whose K/V are in the cache, in order
        readonly object _run = new object();

        public LayeredBackend(EngineOptions o)
        {
            _o = o;
            ModelId = o.ModelId ?? DefaultId(o.ModelPath);
        }

        public static string DefaultId(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(name, @"^(.*)-\d{5}-of-\d{5}$");
            return m.Success ? m.Groups[1].Value : name;
        }

        public override void Load()
        {
            Stopwatch sw = Stopwatch.StartNew();
            _g = GgufModelFile.Open(_o.ModelPath);
            Config = ModelConfig.From(_g);
            Tokenizer = new Tokenizer(_g);
            Template = ChatTemplate.Detect(_g, Tokenizer, _o.ChatTemplate);
            int ctx = _o.ContextLength > 0 ? _o.ContextLength : Math.Min(Config.ContextLength, EngineOptions.DefaultContext);
            ContextLength = ctx;
            Transformer.Threads = _o.Threads;
            if (_o.Playbook != null) foreach (string r in _o.Playbook.Rules) Log.Info("playbook: " + r);
            _stream = new LayerStream(_g, Config, _o.Prefetch, _o.CacheBytes);
            Model = new Transformer(_g, Config, _stream, ctx);
            foreach (string w in Config.Warnings) Log.Warn(w);
            if (Tokenizer.Warning != null) Log.Warn(Tokenizer.Warning);
            Log.Info(string.Format("{0}: {1}, {2} layers, {3:F1} GB of weights, context {4}, chat template {5}",
                ModelId, Config.Arch, Config.Layers, _g.TotalBytes / 1e9, ctx, Template.Name));
            Log.Info(string.Format("RAM: {0:F2} GB per layer slot x {1}, {2:F1} GB of layers kept resident, KV cache up to {3:F1} GB, {4} threads",
                _stream.SlotBytes / 1e9, _o.Prefetch ? 2 : 1, _stream.CachedBytes / 1e9, 2.0 * Config.Layers * Config.KvDim * 4.0 * ctx / 1e9,
                _o.Threads > 0 ? _o.Threads : Environment.ProcessorCount));
            Log.Info("Loaded in " + sw.Elapsed.TotalSeconds.ToString("F1") + " s");
            State = BackendState.Ready;
        }

        public List<int> EncodePrompt(IList<ChatMessage> messages)
        {
            return Tokenizer.Encode(Template.Render(messages), true, true);
        }

        public override GenerationResult Generate(GenerationRequest req, Action<string> onText, Action<int, int> onStage, CancellationToken cancel)
        {
            lock (_run)
            {
                List<int> ids = EncodePrompt(req.Messages);
                if (ids.Count >= ContextLength)
                    throw new BackendException("The prompt is " + ids.Count + " tokens; this server's context is " + ContextLength + " tokens.", 400, "context_length_exceeded");
                int maxTokens = Math.Min(req.MaxTokens, ContextLength - ids.Count);

                // Reuse the K/V of the longest prompt prefix the cache already holds (the earlier turns of a chat).
                int common = 0;
                while (common < _cached.Count && common < ids.Count - 1 && _cached[common] == ids[common]) common++;
                Model.Truncate(common);
                _cached.RemoveRange(common, _cached.Count - common);

                Random rng = req.Seed.HasValue ? new Random(req.Seed.Value) : new Random();
                Decoder utf8 = new UTF8Encoding(false).GetDecoder();
                StopScanner scanner = new StopScanner(req.Stop);
                bool first = true;
                int produced = 0;
                string finish = "length";
                try
                {
                    int[] rest = ids.GetRange(common, ids.Count - common).ToArray();
                    if (common > 0) Log.Debug("reusing " + common + " cached prompt tokens");
                    float[] logits = Model.Forward(rest, common, false, cancel, onStage);
                    _cached.AddRange(rest);
                    while (produced < maxTokens)
                    {
                        int id = Sampler.Sample(logits, req.Temperature, req.TopP, rng);
                        if (Tokenizer.IsEndOfGeneration(id)) { finish = "stop"; break; }
                        produced++;
                        byte[] bytes = Tokenizer.TokenBytes(id, false);
                        char[] chars = new char[utf8.GetCharCount(bytes, 0, bytes.Length, false)];
                        utf8.GetChars(bytes, 0, bytes.Length, chars, 0, false);
                        string text = new string(chars);
                        if (first && text.Length > 0)
                        {
                            if (Tokenizer.StripLeadingSpace && text[0] == ' ') text = text.Substring(1);
                            first = false;
                        }
                        bool stopped;
                        string safe = scanner.Feed(text, out stopped);
                        if (safe.Length > 0) onText(safe);
                        if (stopped) { finish = "stop"; break; }
                        if (produced >= maxTokens) break;
                        logits = Model.Forward(new int[] { id }, _cached.Count, false, cancel, onStage);
                        _cached.Add(id);
                    }
                    string tail = scanner.Flush();   // empty after a stop sequence
                    if (tail.Length > 0) onText(tail);
                    return new GenerationResult(finish, ids.Count, produced);
                }
                catch (OperationCanceledException)
                {
                    _cached.RemoveRange(Model.Length, _cached.Count - Model.Length);
                    return new GenerationResult("cancelled", ids.Count, produced);
                }
                catch
                {
                    Model.Truncate(0);
                    _cached.Clear();
                    throw;
                }
            }
        }

        public override void Dispose()
        {
            if (_stream != null) _stream.Dispose();
        }
    }

    public static class Sampler
    {
        // temperature 0 = greedy. top_p keeps the smallest set of tokens whose probabilities add up to top_p.
        public static int Sample(float[] logits, double temperature, double topP, Random rng)
        {
            int n = logits.Length, best = 0;
            for (int i = 1; i < n; i++) if (logits[i] > logits[best]) best = i;
            if (temperature <= 0) return best;
            float max = logits[best];
            double[] p = new double[n];
            double sum = 0;
            for (int i = 0; i < n; i++) { p[i] = Math.Exp((logits[i] - max) / temperature); sum += p[i]; }
            if (topP >= 1.0)
            {
                double r = rng.NextDouble() * sum;
                for (int i = 0; i < n; i++) { r -= p[i]; if (r <= 0) return i; }
                return best;
            }
            int[] idx = new int[n];
            double[] keys = new double[n];
            for (int i = 0; i < n; i++) { idx[i] = i; keys[i] = -p[i]; }
            Array.Sort(keys, idx);
            double cum = 0, limit = topP * sum;
            int k = 0;
            while (k < n) { cum += p[idx[k]]; k++; if (cum >= limit) break; }
            double pick = rng.NextDouble() * cum;
            for (int i = 0; i < k; i++) { pick -= p[idx[i]]; if (pick <= 0) return idx[i]; }
            return idx[0];
        }
    }
}

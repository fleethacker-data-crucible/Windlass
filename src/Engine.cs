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
        public bool Speculate;               // guess-ahead for greedy replies (batch 1 only)
        public int Batch = 1;                // requests that generate together, one KV cache each (1 = one at a time, as before)
        public bool IntDot;                  // integer dot products with Q8 activations (FastMath.cs)
        public Playbook Playbook;            // the settings' reasons, logged at load (null = set by hand)
        public string PlaybookPath;          // where real runs are recorded (null = not recorded)
        public MachineFacts Machine;
        public const int DefaultContext = 8192;
    }

    public sealed partial class LayeredBackend : Backend
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
        volatile Dictionary<string, object> _loaded;    // facts about the loaded file, for /v1/models (built once by Load)

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
            if (_o.Batch > 1)
            {
                string why = BatchPlan.Check(Config, ctx, _o.Batch, _o.Machine ?? MachineFacts.Detect());
                if (why != null) throw new ArgumentException(why);
            }
            Transformer.IntDot = _o.IntDot;
            if (_o.Playbook != null) foreach (string r in _o.Playbook.Rules) Log.Info("playbook: " + r);
            _stream = new LayerStream(_g, Config, _o.Prefetch, _o.CacheBytes);
            Model = new Transformer(_g, Config, _stream, ctx);
            foreach (string w in Config.Warnings) Log.Warn(w);
            if (Tokenizer.Warning != null) Log.Warn(Tokenizer.Warning);
            Log.Info(string.Format("{0}: {1}, {2} layers, {3:F1} GB of weights, context {4}, chat template {5}",
                ModelId, Config.Arch, Config.Layers, _g.TotalBytes / 1e9, ctx, Template.Name));
            Log.Info(string.Format("RAM: {0:F2} GB per layer slot x {1}, {2:F1} GB of layers kept resident, KV cache up to {3:F1} GB, {4} threads",
                _stream.SlotBytes / 1e9, _o.Prefetch ? 2 : 1, _stream.CachedBytes / 1e9, 2.0 * Config.Layers * Config.KvDim * 2.0 * ctx / 1e9,
                _o.Threads > 0 ? _o.Threads : Environment.ProcessorCount));
            if (_o.Batch > 1)
            {
                Log.Info(string.Format("Batch: up to {0} requests generate together, one KV cache each: up to {1:F2} GB per slot, {2:F2} GB in all ({3:F2} GB more than one request would use)",
                    _o.Batch, BatchPlan.KvBytes(Config, ctx, 1) / 1e9, BatchKvBytes / 1e9, BatchPlan.KvBytes(Config, ctx, _o.Batch - 1) / 1e9));
                if (_o.Speculate) Log.Warn("guess-ahead is off with --batch (it is for one request at a time)");
                StartBatching();
            }
            Log.Info("Loaded in " + sw.Elapsed.TotalSeconds.ToString("F1") + " s");
            _loaded = LoadedFacts(_g, Config, ctx);
            State = BackendState.Ready;
        }

        static Dictionary<string, object> LoadedFacts(GgufModelFile g, ModelConfig c, int ctx)
        {
            Dictionary<string, object> f = new Dictionary<string, object>();
            Dictionary<string, long> bytes = new Dictionary<string, long>();
            foreach (GgufTensor t in g.Tensors)
            {
                string n = GgmlTypes.Name(t.Type);
                long v; bytes.TryGetValue(n, out v); bytes[n] = v + t.SizeBytes;
            }
            string quant = null; long most = -1;
            foreach (KeyValuePair<string, long> kv in bytes) if (kv.Value > most) { most = kv.Value; quant = kv.Key; }
            long file = 0;
            foreach (string p in g.Paths) file += new FileInfo(p).Length;
            f["architecture"] = c.Arch; f["quant"] = quant == null ? null : quant.ToUpperInvariant(); f["layers"] = (long)c.Layers; f["fileBytes"] = file;
            f["contextLength"] = (long)ctx; f["modelContextLength"] = (long)c.ContextLength;
            return f;
        }

        // What is known about this model and what the playbook measured on this machine, for GET /v1/models. Reads
        // snapshots and the playbook's own lock, never the engine lock, so it answers while a reply is running.
        public override Dictionary<string, object> Facts()
        {
            Dictionary<string, object> f = new Dictionary<string, object>();
            Dictionary<string, object> loaded = _loaded;
            if (loaded != null) foreach (KeyValuePair<string, object> kv in loaded) f[kv.Key] = kv.Value;
            f["guessAhead"] = _o.Speculate;
            Playbook p = _o.Playbook;
            if (p != null) foreach (KeyValuePair<string, object> kv in p.ToFacts()) f[kv.Key] = kv.Value;
            return f;
        }

        public List<int> EncodePrompt(IList<ChatMessage> messages)
        {
            return Tokenizer.Encode(Template.Render(messages), true, true);
        }

        public override GenerationResult Generate(GenerationRequest req, Action<string> onText, Action<int, int> onStage, CancellationToken cancel)
        {
            if (_o.Batch > 1) return GenerateBatched(req, onText, onStage, cancel);   // BatchEngine.cs
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
                bool guessing = _o.Speculate && req.Temperature <= 0;   // only greedy replies: then guesses never change the text
                int guessed = 0, accepted = 0;
                List<int> history = new List<int>(ids);                 // prompt + reply, where guesses come from
                Stopwatch clock = Stopwatch.StartNew();
                double promptSeconds = 0;

                // Sends one token's text to the client. Returns true when a stop sequence ends the reply.
                Func<int, bool> emit = delegate(int token)
                {
                    byte[] bytes = Tokenizer.TokenBytes(token, false);
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
                    return stopped;
                };
                try
                {
                    int[] rest = ids.GetRange(common, ids.Count - common).ToArray();
                    if (common > 0) Log.Debug("reusing " + common + " cached prompt tokens");
                    float[] logits = Model.Forward(rest, common, false, cancel, onStage);
                    _cached.AddRange(rest);
                    promptSeconds = clock.Elapsed.TotalSeconds;
                    int id = maxTokens > 0 ? Sampler.Sample(logits, req.Temperature, req.TopP, rng) : -1;
                    while (produced < maxTokens)
                    {
                        if (Tokenizer.IsEndOfGeneration(id)) { finish = "stop"; break; }
                        produced++;
                        history.Add(id);
                        if (emit(id)) { finish = "stop"; break; }
                        if (produced >= maxTokens) break;

                        int room = Math.Min(MaxGuesses, Math.Min(maxTokens - produced, ContextLength - _cached.Count - 1));
                        int[] draft = guessing && room > 0 ? Guess(history, room) : null;
                        if (draft == null)
                        {
                            logits = Model.Forward(new int[] { id }, _cached.Count, false, cancel, onStage);
                            _cached.Add(id);
                            id = Sampler.Sample(logits, req.Temperature, req.TopP, rng);
                            continue;
                        }

                        // Check the token and every guess in one pass; keep the guesses the model agrees with.
                        int[] batch = new int[draft.Length + 1];
                        batch[0] = id;
                        Array.Copy(draft, 0, batch, 1, draft.Length);
                        int basePos = _cached.Count;
                        float[] all = Model.Forward(batch, basePos, true, cancel, onStage);
                        int vocab = all.Length / batch.Length, m = 0;
                        while (m < draft.Length && Argmax(all, (long)m * vocab, vocab) == draft[m]) m++;
                        Model.Truncate(basePos + 1 + m);
                        _cached.Add(id);
                        for (int j = 0; j < m; j++) _cached.Add(draft[j]);
                        guessed += draft.Length; accepted += m;
                        int next = Argmax(all, (long)m * vocab, vocab);
                        bool done = false;
                        for (int j = 0; j < m && !done; j++)
                        {
                            int t = draft[j];
                            if (Tokenizer.IsEndOfGeneration(t)) { finish = "stop"; done = true; break; }
                            produced++;
                            history.Add(t);
                            if (emit(t)) { finish = "stop"; done = true; }
                            else if (produced >= maxTokens) done = true;
                        }
                        if (done) break;
                        id = next;
                    }
                    string tail = scanner.Flush();   // empty after a stop sequence
                    if (tail.Length > 0) onText(tail);
                    if (guessed > 0) Log.Debug("guess-ahead: " + accepted + " of " + guessed + " guesses kept");
                    Observe(ids.Count - common, promptSeconds, produced, clock.Elapsed.TotalSeconds - promptSeconds, guessed, accepted);
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

        // Scores the model on the built-in passage (see Quality.cs). Clears the KV cache, like a new conversation.
        public QualityResult Evaluate(int maxTokens, CancellationToken cancel)
        {
            lock (_run)
            {
                List<int> ids = Tokenizer.Encode(Quality.Passage, true, false);
                int n = Math.Min(Math.Min(ids.Count, maxTokens), ContextLength);
                if (n < 2) throw new ArgumentException("The passage is too short to score with " + maxTokens + " tokens.");
                int[] toks = ids.GetRange(0, n).ToArray();
                Model.Truncate(0);
                _cached.Clear();
                Stopwatch sw = Stopwatch.StartNew();
                float[] logits = Model.Forward(toks, 0, true, cancel, null);
                QualityResult r = new QualityResult();
                r.Seconds = sw.Elapsed.TotalSeconds;
                r.Tokens = n;
                r.Perplexity = Quality.Perplexity(logits, toks);
                Model.Truncate(0);
                return r;
            }
        }

        // Plants `facts` facts in a synthetic log of about `tokens` prompt tokens and asks about them (see Fidelity.cs),
        // through the normal chat path: greedy, small max_tokens, one request per question. The log is sized against this
        // model's own tokenizer, so `tokens` counts the whole prompt. Holds the engine for the whole check, like Evaluate.
        public FidelityResult CheckFidelity(int facts, int tokens, int questions, int seed, CancellationToken cancel)
        {
            lock (_run)
            {
                int target = Math.Min(tokens, ContextLength - Fidelity.ReplyTokens - 8);
                int chars = Fidelity.CharsForTokens(target), bestTokens = 0;
                FidelityCase best = null;
                for (int pass = 0; pass < 4; pass++)
                {
                    FidelityCase c = Fidelity.Generate(seed, facts, chars);
                    int n = EncodePrompt(Fidelity.Prompt(c, c.Facts[0])).Count;
                    if (n <= target && n > bestTokens) { best = c; bestTokens = n; }
                    if (n <= target && n >= target * 0.9) break;
                    int next = (int)(chars * (double)target / Math.Max(n, 1) * 0.98);
                    if (next == chars) next += n > target ? -1 : 1;
                    chars = Math.Max(200, Math.Min(Fidelity.MaxChars, next));
                }
                if (best == null)
                {
                    best = Fidelity.Generate(seed, facts, 200);   // the least filler there can be
                    bestTokens = EncodePrompt(Fidelity.Prompt(best, best.Facts[0])).Count;
                    if (bestTokens > ContextLength - Fidelity.ReplyTokens - 8)
                        throw new ArgumentException(facts + " planted facts need a prompt of at least " + bestTokens + " tokens; this context is " + ContextLength + ". Use fewer facts or a longer context.");
                }
                FidelityResult r = Fidelity.Run(this, best, questions, cancel);
                r.PromptTokens = bestTokens;
                return r;
            }
        }

        public const int MaxGuesses = 8;

        // Prompt-lookup guessing: find the latest earlier place where the last few tokens appeared, and guess that
        // what followed them then follows them now. Edits, summaries and code repeat a lot of their input.
        public static int[] Guess(List<int> history, int max)
        {
            int n = history.Count;
            for (int len = Math.Min(3, n - 1); len >= 1; len--)
            {
                for (int start = n - len - 1; start >= 0; start--)
                {
                    int k = 0;
                    while (k < len && history[start + k] == history[n - len + k]) k++;
                    if (k < len) continue;
                    int from = start + len, count = Math.Min(max, n - from);
                    if (count <= 0) continue;
                    return history.GetRange(from, count).ToArray();
                }
            }
            return null;
        }

        static int Argmax(float[] a, long offset, int n)
        {
            int best = 0;
            for (int i = 1; i < n; i++) if (a[offset + i] > a[offset + best]) best = i;
            return best;
        }

        // Real runs teach the playbook: speed per token and how often guesses were right, saved for the next start.
        void Observe(int promptTokens, double promptSeconds, int tokens, double seconds, int guessed, int accepted)
        {
            if (_o.Playbook == null || _o.PlaybookPath == null) return;
            _o.Playbook.Observe(promptTokens, promptSeconds, tokens, seconds, guessed, accepted);
            try { _o.Playbook.Save(_o.PlaybookPath, _o.Machine); }
            catch (IOException e) { Log.Debug("playbook not saved: " + e.Message); }
            catch (UnauthorizedAccessException e) { Log.Debug("playbook not saved: " + e.Message); }
        }

        public override void Dispose()
        {
            StopBatching();
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

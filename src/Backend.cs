// What the HTTP server talks to: a backend that turns a request into streamed text. C# 5 only.
// The real one is LayeredBackend (Engine.cs); StubBackend needs no model and can be made as slow as a 70B model,
// for wiring tests (--stub) and the API tests.
using System;
using System.Collections.Generic;
using System.Threading;

namespace Windlass
{
    public sealed class BackendException : Exception
    {
        public readonly int Status;
        public readonly string Kind;
        public BackendException(string message) : this(message, 400, "invalid_request_error") { }
        public BackendException(string message, int status, string kind) : base(message) { Status = status; Kind = kind; }
    }

    public sealed class GenerationRequest
    {
        public List<ChatMessage> Messages = new List<ChatMessage>();
        public int MaxTokens = 1024;
        public double Temperature = 1.0, TopP = 1.0;
        public List<string> Stop = new List<string>();
        public int? Seed;
    }

    public sealed class GenerationResult
    {
        public string FinishReason;   // stop, length, cancelled
        public int PromptTokens, CompletionTokens;
        public GenerationResult(string finish, int prompt, int completion) { FinishReason = finish; PromptTokens = prompt; CompletionTokens = completion; }
    }

    public enum BackendState { Loading, Ready, Error }

    public abstract class Backend : IDisposable
    {
        public string ModelId = "model";
        public int ContextLength = 4096;
        public volatile BackendState State = BackendState.Loading;
        public string LoadError;

        // Blocking; the server calls it on a background thread.
        public abstract void Load();

        // Blocking. onText gets each new piece of text; onStage(done, total) reports layer progress.
        public abstract GenerationResult Generate(GenerationRequest request, Action<string> onText, Action<int, int> onStage, CancellationToken cancel);

        // What this backend knows about its model and has measured, for the `windlass` object on /v1/models
        // (ModelFacts.Shape lists the keys). A plain dictionary of nested dictionaries; keys it does not know are left
        // out and published as null. Must not wait for a running generation.
        public virtual Dictionary<string, object> Facts() { return new Dictionary<string, object>(); }

        public virtual void Dispose() { }
    }

    // The `windlass` extension object on a model card: a fixed set of keys, every one null until a backend reports it.
    public static class ModelFacts
    {
        public const string Schema = "windlass.model/1";

        static Dictionary<string, object> Figure()
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["value"] = null; d["predicted"] = null; d["observed"] = null; d["measured"] = false; d["source"] = "none"; d["samples"] = null;
            return d;
        }

        public static Dictionary<string, object> Shape()
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["schema"] = Schema;
            m["architecture"] = null; m["quant"] = null; m["layers"] = null; m["fileBytes"] = null;
            m["contextLength"] = null; m["modelContextLength"] = null; m["guessAhead"] = null;
            Dictionary<string, object> speed = new Dictionary<string, object>();
            speed["secPerToken"] = Figure(); speed["secPerPromptToken"] = Figure(); speed["limit"] = null;
            m["speed"] = speed;
            Dictionary<string, object> q = new Dictionary<string, object>();
            q["perplexity"] = null; q["tokens"] = null; q["measuredAt"] = null;
            m["quality"] = q;
            Dictionary<string, object> f = new Dictionary<string, object>();
            f["score"] = null; f["correct"] = null; f["total"] = null; f["promptTokens"] = null; f["version"] = null; f["measuredAt"] = null;
            m["fidelity"] = f;
            Dictionary<string, object> queue = new Dictionary<string, object>();
            queue["length"] = null; queue["max"] = null;
            m["queue"] = queue;
            return m;
        }

        // The shape with a backend's facts laid over it. Unknown keys are dropped so the object stays stable.
        public static Dictionary<string, object> Build(Dictionary<string, object> facts)
        {
            Dictionary<string, object> m = Shape();
            if (facts != null) Overlay(m, facts);
            return m;
        }

        public static void Overlay(Dictionary<string, object> shape, Dictionary<string, object> facts)
        {
            foreach (KeyValuePair<string, object> kv in facts)
            {
                object slot;
                if (!shape.TryGetValue(kv.Key, out slot) || kv.Key == "schema") continue;
                Dictionary<string, object> inner = slot as Dictionary<string, object>, over = kv.Value as Dictionary<string, object>;
                if (inner != null && over != null) Overlay(inner, over);
                else if (inner == null && over == null) shape[kv.Key] = kv.Value;
            }
        }
    }

    // Holds back streamed text that could be the start of a stop sequence. Feed returns the text that is safe to send
    // now; when a stop sequence is hit, it and anything after it are dropped.
    public sealed class StopScanner
    {
        readonly List<string> _stops = new List<string>();
        readonly int _hold;
        string _buffer = "";

        public StopScanner(IEnumerable<string> stops)
        {
            int max = 1;
            foreach (string s in stops) if (!string.IsNullOrEmpty(s)) { _stops.Add(s); max = Math.Max(max, s.Length); }
            _hold = max - 1;
        }

        public string Feed(string text, out bool stopped)
        {
            _buffer += text;
            int cut = -1;
            foreach (string s in _stops)
            {
                int i = _buffer.IndexOf(s, StringComparison.Ordinal);
                if (i >= 0 && (cut < 0 || i < cut)) cut = i;
            }
            string outp;
            if (cut >= 0) { outp = _buffer.Substring(0, cut); _buffer = ""; stopped = true; return outp; }
            stopped = false;
            int keep = Math.Min(_hold, _buffer.Length);
            // never split a surrogate pair
            if (keep > 0 && keep < _buffer.Length && char.IsLowSurrogate(_buffer[_buffer.Length - keep])) keep++;
            outp = _buffer.Substring(0, _buffer.Length - keep);
            _buffer = _buffer.Substring(_buffer.Length - keep);
            return outp;
        }

        public string Flush() { string o = _buffer; _buffer = ""; return o; }
    }

    // Replies with a fixed sentence, one word per "layer pass".
    public sealed class StubBackend : Backend
    {
        public string Reply = "Hello from Windlass, layer by layer.";
        public int TokenDelayMs, LoadDelayMs;
        public BackendException FailWith;
        public readonly List<GenerationRequest> Calls = new List<GenerationRequest>();
        public readonly ManualResetEvent Cancelled = new ManualResetEvent(false);

        public StubBackend(string modelId) { ModelId = modelId; }

        public override void Load()
        {
            if (LoadDelayMs > 0) Thread.Sleep(LoadDelayMs);
            State = BackendState.Ready;
        }

        public override GenerationResult Generate(GenerationRequest request, Action<string> onText, Action<int, int> onStage, CancellationToken cancel)
        {
            lock (Calls) Calls.Add(request);
            if (FailWith != null) throw FailWith;
            int promptTokens = 0;
            foreach (ChatMessage m in request.Messages) promptTokens += m.Content.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).Length;
            string[] words = Reply.Split(' ');
            StopScanner scanner = new StopScanner(request.Stop);
            int produced = 0;
            for (int i = 0; i < words.Length; i++)
            {
                if (produced >= request.MaxTokens) { onText(scanner.Flush()); return new GenerationResult("length", promptTokens, produced); }
                if (cancel.WaitHandle.WaitOne(TokenDelayMs) || cancel.IsCancellationRequested)
                {
                    Cancelled.Set();
                    return new GenerationResult("cancelled", promptTokens, produced);
                }
                if (onStage != null) onStage(1, 1);
                produced++;
                bool stopped;
                string text = scanner.Feed(i == 0 ? words[i] : " " + words[i], out stopped);
                if (text.Length > 0) onText(text);
                if (stopped) return new GenerationResult("stop", promptTokens, produced);
            }
            onText(scanner.Flush());
            return new GenerationResult("stop", promptTokens, produced);
        }
    }
}

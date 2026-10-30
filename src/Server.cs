// OpenAI-compatible HTTP API on loopback (HttpListener). C# 5 only.
// Endpoints: POST /v1/chat/completions (stream or not), GET /v1/models, GET /v1/models/{id}, GET /health, and the
// FleetHacker GET /__fleethacker/id + POST /__fleethacker/shutdown (edge-ops PORTS.md).
// One generation runs at a time (or, with --batch N, up to N together); others wait in a bounded FIFO queue (beyond it:
// 503). A streaming request gets its
// headers and an SSE comment as soon as it is accepted, then ": keepalive" comments while it waits or while a slow
// layer pass runs, so a router can tell slow from dead.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Windlass
{
    public sealed class ServerSettings
    {
        public int Port = 8777;
        public string ApiKey;                  // require "Authorization: Bearer <key>" when set
        public int KeepaliveMs = 15000;        // between ": keepalive" comments; keep under 60 s
        public int MaxQueue = 8;               // requests allowed to wait behind the running one
        public int DefaultMaxTokens = 1024;
        public string Version = "0.0.0";
        public string ScriptPath;              // reported by /__fleethacker/id
        public bool LoadOnStartup = true;
    }

    // The running jobs (one, or the batch size) and a bounded FIFO of waiting ones.
    public sealed class Scheduler
    {
        public sealed class Ticket { public bool Granted; public string Id; }

        readonly object _gate = new object();
        readonly LinkedList<Ticket> _queue = new LinkedList<Ticket>();
        readonly int _max, _slots;
        int _running;

        public Scheduler(int maxQueue) : this(maxQueue, 1) { }
        public Scheduler(int maxQueue, int slots) { _max = maxQueue; _slots = Math.Max(1, slots); }

        public bool Busy { get { lock (_gate) return _running > 0; } }
        public int Waiting { get { lock (_gate) return _queue.Count; } }

        // Claims a place now (so a full queue is a clean 503). Position 0 means it runs at once.
        public Ticket Reserve(string id, out int position)
        {
            lock (_gate)
            {
                Ticket t = new Ticket { Id = id };
                if (_running < _slots) { _running++; t.Granted = true; position = 0; return t; }
                if (_queue.Count >= _max) throw new BackendException("Windlass is busy and its queue of " + _max + " is full", 503, "server_busy");
                _queue.AddLast(t);
                position = _queue.Count;
                return t;
            }
        }

        // True once the ticket has the turn; false after timeoutMs.
        public bool Wait(Ticket t, int timeoutMs)
        {
            lock (_gate)
            {
                if (!t.Granted) Monitor.Wait(_gate, timeoutMs);
                return t.Granted;
            }
        }

        // Gives up a place in line, or the turn itself.
        public void Abandon(Ticket t)
        {
            lock (_gate)
            {
                if (t.Granted) { ReleaseLocked(); return; }
                _queue.Remove(t);
            }
        }

        public void Release() { lock (_gate) ReleaseLocked(); }

        void ReleaseLocked()
        {
            if (_queue.Count > 0)
            {
                Ticket next = _queue.First.Value;
                _queue.RemoveFirst();
                next.Granted = true;
                Monitor.PulseAll(_gate);
            }
            else _running--;
        }
    }

    public sealed class ApiServer : IDisposable
    {
        public const string ToolId = "windlass";

        readonly Backend _backend;
        readonly ServerSettings _s;
        readonly Scheduler _sched;
        readonly HttpListener _listener = new HttpListener();
        readonly ManualResetEvent _stopped = new ManualResetEvent(false);
        Thread _accept;

        public ApiServer(Backend backend, ServerSettings settings)
        {
            _backend = backend;
            _s = settings;
            _sched = new Scheduler(settings.MaxQueue, backend.MaxConcurrent);
        }

        public WaitHandle Stopped { get { return _stopped; } }

        public void Start()
        {
            _listener.Prefixes.Add("http://127.0.0.1:" + _s.Port + "/");
            _listener.Prefixes.Add("http://localhost:" + _s.Port + "/");
            _listener.Start();
            if (_s.LoadOnStartup && _backend.State == BackendState.Loading)
            {
                Thread load = new Thread(LoadModel) { IsBackground = true, Name = "windlass-load" };
                load.Start();
            }
            _accept = new Thread(AcceptLoop) { IsBackground = true, Name = "windlass-accept" };
            _accept.Start();
        }

        void LoadModel()
        {
            try { _backend.Load(); }
            catch (Exception e)
            {
                _backend.LoadError = e.GetType().Name + ": " + e.Message;
                _backend.State = BackendState.Error;
                Log.Error("model failed to load: " + _backend.LoadError);
            }
        }

        void AcceptLoop()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = _listener.GetContext(); }
                catch (HttpListenerException) { break; }
                catch (ObjectDisposedException) { break; }
                catch (InvalidOperationException) { break; }
                Thread t = new Thread(delegate() { Handle(ctx); }) { IsBackground = true, Name = "windlass-request" };
                t.Start();
            }
        }

        public void Stop()
        {
            try { _listener.Stop(); } catch (ObjectDisposedException) { }
            _stopped.Set();
        }

        public void Dispose()
        {
            Stop();
            try { _listener.Close(); } catch (ObjectDisposedException) { }
        }

        // ---- request handling ----

        static bool IsLoopbackHost(string hostPort, int port)
        {
            if (string.IsNullOrEmpty(hostPort)) return false;
            string h = hostPort.ToLowerInvariant();
            return h == "127.0.0.1:" + port || h == "localhost:" + port || h == "[::1]:" + port;
        }

        static bool IsLoopbackOrigin(string origin)
        {
            Uri u;
            if (!Uri.TryCreate(origin, UriKind.Absolute, out u) || u.Scheme != "http") return false;
            return u.Host == "127.0.0.1" || u.Host == "localhost" || u.Host == "[::1]";
        }

        void Handle(HttpListenerContext ctx)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;
            try
            {
                string path = req.Url.AbsolutePath;
                string origin = req.Headers["Origin"];
                if (!IsLoopbackHost(req.Headers["Host"], _s.Port))
                {
                    Error(res, 403, "Host header must be 127.0.0.1 or localhost", "forbidden");
                    return;
                }
                if (origin != null)
                {
                    bool fleet = path.StartsWith("/__fleethacker/", StringComparison.Ordinal);
                    bool ok = fleet ? IsLoopbackHost(OriginHostPort(origin), _s.Port) : IsLoopbackOrigin(origin);
                    if (!ok) { Error(res, 403, "cross-origin requests are allowed from loopback pages only", "forbidden"); return; }
                    res.AddHeader("Access-Control-Allow-Origin", origin);
                    res.AddHeader("Vary", "Origin");
                }
                if (req.HttpMethod == "OPTIONS")
                {
                    res.AddHeader("Access-Control-Allow-Methods", "GET, POST, OPTIONS");
                    res.AddHeader("Access-Control-Allow-Headers", "Authorization, Content-Type, X-Api-Key");
                    res.StatusCode = 204;
                    res.Close();
                    return;
                }
                Route(ctx, path);
            }
            catch (Exception e)
            {
                Log.Error("unhandled error: " + e);
                try { Error(res, 500, e.GetType().Name + ": " + e.Message, "server_error"); } catch { }
            }
        }

        static string OriginHostPort(string origin)
        {
            Uri u;
            return Uri.TryCreate(origin, UriKind.Absolute, out u) && u.Scheme == "http" ? u.Host + ":" + u.Port : null;
        }

        void Route(HttpListenerContext ctx, string path)
        {
            HttpListenerRequest req = ctx.Request;
            HttpListenerResponse res = ctx.Response;
            string method = req.HttpMethod;
            if (path == "/__fleethacker/id" && method == "GET")
            {
                Dictionary<string, object> id = new Dictionary<string, object>();
                id["tool"] = ToolId;
                id["version"] = _s.Version;
                id["pid"] = Process.GetCurrentProcess().Id;
                if (_s.ScriptPath != null) id["path"] = _s.ScriptPath;
                Send(res, 200, id);
                return;
            }
            if (path == "/__fleethacker/shutdown" && method == "POST")
            {
                if (req.Headers["X-FleetHacker-Shutdown"] != "1") { Error(res, 400, "missing X-FleetHacker-Shutdown: 1", "invalid_request_error"); return; }
                Send(res, 200, new Dictionary<string, object> { { "stopping", true } });
                Log.Info("shutdown requested over HTTP");
                ThreadPool.QueueUserWorkItem(delegate { Thread.Sleep(200); Stop(); });
                return;
            }
            if (path == "/health" && method == "GET")
            {
                Dictionary<string, object> h = new Dictionary<string, object>();
                h["status"] = StateName();
                h["model"] = _backend.ModelId;
                h["busy"] = _sched.Busy;
                h["queued"] = _sched.Waiting;
                h["error"] = _backend.LoadError;
                Send(res, 200, h);
                return;
            }
            if (path == "/v1/models" || path.StartsWith("/v1/models/", StringComparison.Ordinal))
            {
                if (method != "GET") { Error(res, 405, "use GET", "invalid_request_error"); return; }
                if (!Authorized(req, res)) return;
                if (path == "/v1/models")
                {
                    Dictionary<string, object> list = new Dictionary<string, object>();
                    list["object"] = "list";
                    list["data"] = new List<object> { ModelCard() };
                    Send(res, 200, list);
                    return;
                }
                string id = Uri.UnescapeDataString(path.Substring("/v1/models/".Length));
                if (id != _backend.ModelId) { Error(res, 404, "model '" + id + "' not found", "not_found_error"); return; }
                Send(res, 200, ModelCard());
                return;
            }
            if (path == "/v1/chat/completions")
            {
                if (method != "POST") { Error(res, 405, "use POST", "invalid_request_error"); return; }
                if (!Authorized(req, res)) return;
                ChatCompletions(ctx);
                return;
            }
            Error(res, 404, "no route for " + method + " " + path, "not_found_error");
        }

        string StateName()
        {
            switch (_backend.State)
            {
                case BackendState.Ready: return "ready";
                case BackendState.Error: return "error";
                default: return "loading";
            }
        }

        Dictionary<string, object> ModelCard()
        {
            Dictionary<string, object> m = new Dictionary<string, object>();
            m["id"] = _backend.ModelId;
            m["object"] = "model";
            m["created"] = 0;
            m["owned_by"] = "windlass";
            m["context_length"] = _backend.ContextLength;
            m["status"] = StateName();
            return m;
        }

        bool Authorized(HttpListenerRequest req, HttpListenerResponse res)
        {
            if (string.IsNullOrEmpty(_s.ApiKey)) return true;
            string header = req.Headers["Authorization"] ?? "";
            string key = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? header.Substring(7) : req.Headers["X-Api-Key"];
            if (key != null && FixedTimeEquals(key, _s.ApiKey)) return true;
            Error(res, 401, "missing or wrong API key", "authentication_error");
            return false;
        }

        static bool FixedTimeEquals(string a, string b)
        {
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < a.Length && i < b.Length; i++) diff |= a[i] ^ b[i];
            return diff == 0;
        }

        // ---- /v1/chat/completions ----

        public static GenerationRequest ParseRequest(object parsed, Backend backend, int defaultMaxTokens)
        {
            Dictionary<string, object> body = parsed as Dictionary<string, object>;
            if (body == null) throw new BackendException("request body must be a JSON object");
            object model;
            if (body.TryGetValue("model", out model) && model != null && !(model is string && ((string)model == "" || (string)model == backend.ModelId)))
                throw new BackendException("model '" + model + "' not found; this server has '" + backend.ModelId + "'", 404, "not_found_error");
            object rawMessages;
            List<object> messages = body.TryGetValue("messages", out rawMessages) ? rawMessages as List<object> : null;
            if (messages == null || messages.Count == 0) throw new BackendException("'messages' must be a non-empty array");
            GenerationRequest g = new GenerationRequest();
            foreach (object o in messages)
            {
                Dictionary<string, object> m = o as Dictionary<string, object>;
                if (m == null || !(Get(m, "role") is string)) throw new BackendException("each message needs a 'role'");
                g.Messages.Add(new ChatMessage((string)m["role"], ContentText(Get(m, "content"))));
            }
            object n = Get(body, "n");
            if (n != null && !(n is double && (double)n == 1)) throw new BackendException("only n=1 is supported");
            object tools = Get(body, "tools");
            if (tools is List<object> && ((List<object>)tools).Count > 0) Log.Warn("request carries tools; Windlass does not do tool calling and ignores them");

            object mt = Get(body, "max_completion_tokens") ?? Get(body, "max_tokens");
            if (mt == null) g.MaxTokens = defaultMaxTokens;
            else if (mt is double && (double)mt >= 1 && (double)mt == Math.Floor((double)mt) && (double)mt <= int.MaxValue) g.MaxTokens = (int)(double)mt;
            else throw new BackendException("'max_tokens' must be a positive integer");
            g.Temperature = Number(body, "temperature", 1.0, 0, 2);
            g.TopP = Number(body, "top_p", 1.0, 0, 1);

            object stop = Get(body, "stop");
            if (stop is string) g.Stop.Add((string)stop);
            else if (stop is List<object>)
            {
                foreach (object s in (List<object>)stop)
                {
                    if (!(s is string)) throw new BackendException("'stop' must be a string or an array of strings");
                    g.Stop.Add((string)s);
                }
            }
            else if (stop != null) throw new BackendException("'stop' must be a string or an array of strings");

            object seed = Get(body, "seed");
            if (seed is double && (double)seed == Math.Floor((double)seed)) g.Seed = (int)(long)(double)seed;
            else if (seed != null) throw new BackendException("'seed' must be an integer");
            return g;
        }

        static object Get(Dictionary<string, object> o, string key)
        {
            object v;
            return o.TryGetValue(key, out v) ? v : null;
        }

        static double Number(Dictionary<string, object> body, string key, double dflt, double low, double high)
        {
            object v = Get(body, key);
            if (v == null) return dflt;
            if (!(v is double) || (double)v < low || (double)v > high) throw new BackendException("'" + key + "' must be a number between " + low + " and " + high);
            return (double)v;
        }

        // A message's content: a string, or an array of parts whose "text" parts are joined.
        static string ContentText(object content)
        {
            if (content == null) return "";
            if (content is string) return (string)content;
            List<object> parts = content as List<object>;
            if (parts == null) throw new BackendException("message 'content' must be a string or an array of parts");
            StringBuilder sb = new StringBuilder();
            foreach (object p in parts)
            {
                Dictionary<string, object> part = p as Dictionary<string, object>;
                if (part == null) continue;
                if ((Get(part, "type") as string) == "text" && Get(part, "text") is string) sb.Append((string)part["text"]);
                else throw new BackendException("only text content parts are supported");
            }
            return sb.ToString();
        }

        void ChatCompletions(HttpListenerContext ctx)
        {
            HttpListenerResponse res = ctx.Response;
            string text;
            using (StreamReader r = new StreamReader(ctx.Request.InputStream, new UTF8Encoding(false))) text = r.ReadToEnd();
            object parsed;
            try { parsed = Json.Parse(text); }
            catch (Exception) { Error(res, 400, "request body is not valid JSON", "invalid_request_error"); return; }
            GenerationRequest greq;
            try { greq = ParseRequest(parsed, _backend, _s.DefaultMaxTokens); }
            catch (BackendException e) { Error(res, e.Status, e.Message, e.Kind); return; }
            if (_backend.State == BackendState.Loading)
            {
                res.AddHeader("Retry-After", "30");
                Error(res, 503, _backend.ModelId + " is still loading", "model_loading");
                return;
            }
            if (_backend.State == BackendState.Error)
            {
                Error(res, 503, _backend.ModelId + " failed to load: " + _backend.LoadError, "model_load_failed");
                return;
            }

            string rid = "chatcmpl-" + Guid.NewGuid().ToString("N").Substring(0, 24);
            Scheduler.Ticket ticket;
            int position;
            try { ticket = _sched.Reserve(rid, out position); }
            catch (BackendException e)
            {
                res.AddHeader("Retry-After", "60");
                Error(res, e.Status, e.Message, e.Kind);
                return;
            }
            if (position > 0) Log.Info(rid + " queued at position " + position);

            Dictionary<string, object> body = (Dictionary<string, object>)parsed;
            bool stream = Get(body, "stream") is bool && (bool)body["stream"];
            Dictionary<string, object> so = Get(body, "stream_options") as Dictionary<string, object>;
            bool includeUsage = so != null && Get(so, "include_usage") is bool && (bool)so["include_usage"];
            long created = (long)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds;

            if (stream) RunStream(res, rid, created, greq, ticket, includeUsage);
            else RunPlain(res, rid, created, greq, ticket);
        }

        sealed class JobEvent
        {
            public string Text;
            public GenerationResult Done;
            public BackendException Error;
        }

        // Starts the generation once the ticket has the turn. The worker releases the turn when it finishes.
        BlockingCollection<JobEvent> StartJob(string rid, GenerationRequest greq, CancellationTokenSource cts)
        {
            BlockingCollection<JobEvent> events = new BlockingCollection<JobEvent>();
            Log.Info(rid + " started (" + _sched.Waiting + " waiting)");
            Thread worker = new Thread(delegate()
            {
                Stopwatch sw = Stopwatch.StartNew();
                try
                {
                    GenerationResult r = _backend.Generate(greq,
                        delegate(string t) { if (t.Length > 0) events.Add(new JobEvent { Text = t }); },
                        delegate(int done, int total) { if (Log.Verbose) Log.Debug(rid + " stage " + done + "/" + total); },
                        cts.Token);
                    double secs = sw.Elapsed.TotalSeconds;
                    Log.Info(string.Format("{0} {1}: {2} prompt + {3} completion tokens in {4:F1}s ({5:F2} tok/s)",
                        rid, r.FinishReason, r.PromptTokens, r.CompletionTokens, secs, secs > 0 ? r.CompletionTokens / secs : 0));
                    events.Add(new JobEvent { Done = r });
                }
                catch (BackendException e) { events.Add(new JobEvent { Error = e }); }
                catch (Exception e)
                {
                    Log.Error(rid + " generation failed: " + e);
                    events.Add(new JobEvent { Error = new BackendException(e.GetType().Name + ": " + e.Message, 500, "server_error") });
                }
                finally
                {
                    _sched.Release();
                    events.CompleteAdding();
                }
            }) { IsBackground = true, Name = "windlass-" + rid };
            worker.Start();
            return events;
        }

        void RunPlain(HttpListenerResponse res, string rid, long created, GenerationRequest greq, Scheduler.Ticket ticket)
        {
            while (!_sched.Wait(ticket, 1000)) { }
            CancellationTokenSource cts = new CancellationTokenSource();
            BlockingCollection<JobEvent> events = StartJob(rid, greq, cts);
            StringBuilder sb = new StringBuilder();
            GenerationResult result = null;
            foreach (JobEvent ev in events.GetConsumingEnumerable())
            {
                if (ev.Error != null) { Error(res, ev.Error.Status, ev.Error.Message, ev.Error.Kind); return; }
                if (ev.Text != null) sb.Append(ev.Text);
                if (ev.Done != null) result = ev.Done;
            }
            Dictionary<string, object> msg = new Dictionary<string, object> { { "role", "assistant" }, { "content", sb.ToString() } };
            Dictionary<string, object> choice = new Dictionary<string, object> { { "index", 0 }, { "finish_reason", Finish(result) }, { "message", msg } };
            Dictionary<string, object> outp = new Dictionary<string, object>();
            outp["id"] = rid;
            outp["object"] = "chat.completion";
            outp["created"] = created;
            outp["model"] = _backend.ModelId;
            outp["choices"] = new List<object> { choice };
            outp["usage"] = Usage(result);
            Send(res, 200, outp);
        }

        void RunStream(HttpListenerResponse res, string rid, long created, GenerationRequest greq, Scheduler.Ticket ticket, bool includeUsage)
        {
            res.StatusCode = 200;
            res.ContentType = "text/event-stream; charset=utf-8";
            res.AddHeader("Cache-Control", "no-cache");
            res.AddHeader("X-Accel-Buffering", "no");
            res.SendChunked = true;
            Stream outp = res.OutputStream;
            CancellationTokenSource cts = null;
            bool started = false;
            try
            {
                // Sent before any waiting, so the headers and a first byte reach the client now.
                Write(outp, ": accepted\n\n");
                Write(outp, Sse(Chunk(rid, created, new Dictionary<string, object> { { "role", "assistant" }, { "content", "" } }, null)));
                while (!_sched.Wait(ticket, _s.KeepaliveMs)) Write(outp, ": keepalive\n\n");
                started = true;
                cts = new CancellationTokenSource();
                BlockingCollection<JobEvent> events = StartJob(rid, greq, cts);
                while (true)
                {
                    JobEvent ev;
                    if (!events.TryTake(out ev, _s.KeepaliveMs))
                    {
                        if (events.IsCompleted) break;
                        Write(outp, ": keepalive\n\n");
                        continue;
                    }
                    if (ev.Error != null)
                    {
                        Dictionary<string, object> err = new Dictionary<string, object> { { "message", ev.Error.Message }, { "type", ev.Error.Kind } };
                        Write(outp, Sse(new Dictionary<string, object> { { "error", err } }));
                    }
                    else if (ev.Text != null) Write(outp, Sse(Chunk(rid, created, new Dictionary<string, object> { { "content", ev.Text } }, null)));
                    else if (ev.Done != null)
                    {
                        Write(outp, Sse(Chunk(rid, created, new Dictionary<string, object>(), Finish(ev.Done))));
                        if (includeUsage)
                        {
                            Dictionary<string, object> u = new Dictionary<string, object>();
                            u["id"] = rid; u["object"] = "chat.completion.chunk"; u["created"] = created; u["model"] = _backend.ModelId;
                            u["choices"] = new List<object>();
                            u["usage"] = Usage(ev.Done);
                            Write(outp, Sse(u));
                        }
                    }
                }
                Write(outp, "data: [DONE]\n\n");
                res.Close();
            }
            catch (Exception e)
            {
                if (!(e is IOException || e is HttpListenerException || e is ObjectDisposedException || e is InvalidOperationException)) throw;
                Log.Info(rid + " client went away; cancelling");
                if (started) { if (cts != null) cts.Cancel(); }
                else _sched.Abandon(ticket);
                try { res.Abort(); } catch { }
            }
        }

        static string Finish(GenerationResult r)
        {
            // A cancelled job only finishes after its client left, so this is rarely seen.
            return r != null && r.FinishReason == "length" ? "length" : "stop";
        }

        static Dictionary<string, object> Usage(GenerationResult r)
        {
            int p = r != null ? r.PromptTokens : 0, c = r != null ? r.CompletionTokens : 0;
            return new Dictionary<string, object> { { "prompt_tokens", p }, { "completion_tokens", c }, { "total_tokens", p + c } };
        }

        Dictionary<string, object> Chunk(string rid, long created, Dictionary<string, object> delta, string finish)
        {
            Dictionary<string, object> c = new Dictionary<string, object>();
            c["id"] = rid; c["object"] = "chat.completion.chunk"; c["created"] = created; c["model"] = _backend.ModelId;
            c["choices"] = new List<object> { new Dictionary<string, object> { { "index", 0 }, { "delta", delta }, { "finish_reason", finish } } };
            return c;
        }

        static string Sse(object data) { return "data: " + Json.Write(data) + "\n\n"; }

        static void Write(Stream s, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            s.Write(b, 0, b.Length);
            s.Flush();
        }

        static void Send(HttpListenerResponse res, int status, object body)
        {
            byte[] b = Encoding.UTF8.GetBytes(Json.Write(body));
            res.StatusCode = status;
            res.ContentType = "application/json; charset=utf-8";
            res.ContentLength64 = b.Length;
            res.OutputStream.Write(b, 0, b.Length);
            res.Close();
        }

        static void Error(HttpListenerResponse res, int status, string message, string kind)
        {
            Dictionary<string, object> err = new Dictionary<string, object> { { "message", message }, { "type", kind } };
            Send(res, status, new Dictionary<string, object> { { "error", err } });
        }
    }
}

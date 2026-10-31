// Tests for the Fairlead `deep-local` route contract (Fairlead's integrations/windlass/README.md): the requests Fairlead
// sends to Windlass and the answers it needs. They run the real server against a stub model (no model, no heat) and
// check each item of that README's checklist that a program can check. C# 5 only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace Windlass.Tests
{
    public static partial class Runner
    {
        const string FairleadModel = "Llama-3.3-70B-Instruct-Q4_K_M";
        const string FairleadKey = "fl-client-key-for-tests";

        // What Fairlead sends: its client key as a Bearer token, the privacy header for local-only requests, a request id,
        // a User-Agent, and a body with the target model without the provider prefix plus fields Windlass ignores.
        static readonly string[] FairleadHeaders = { "Authorization", "Bearer " + FairleadKey, "X-Fairlead-Privacy", "local", "X-Fairlead-Route", "deep-local", "X-Request-Id", "req-7f3a" };

        static string FairleadBody(bool stream)
        {
            return "{\"model\":\"" + FairleadModel + "\",\"messages\":[{\"role\":\"system\",\"content\":\"Be brief.\"},{\"role\":\"user\",\"content\":\"hi there\"}],"
                + "\"temperature\":0.2,\"top_p\":0.9,\"max_tokens\":64,\"user\":\"batch-job-3\",\"metadata\":{\"route\":\"deep-local\"}"
                + (stream ? ",\"stream\":true,\"stream_options\":{\"include_usage\":true}" : "") + "}";
        }

        static TestServer FairleadServer(int delayMs, ServerSettings s)
        {
            StubBackend b = new StubBackend(FairleadModel);
            b.TokenDelayMs = delayMs;
            return new TestServer(b, s ?? new ServerSettings());
        }

        // Checklist: "GET /v1/models lists the model and its context length".
        static void FairleadModelList()
        {
            using (FairleadServer(0, null))
            {
                Resp m = Call("GET", "/v1/models", null, FairleadHeaders);
                Equal(200, m.Status, "models status");
                Equal("list", (string)Path_(m.Json, "object"), "object");
                Equal(FairleadModel, (string)Path_(m.Json, "data", 0, "id"), "the id Fairlead's Fetch model list shows (route target windlass/<id>)");
                Check((double)Path_(m.Json, "data", 0, "context_length") > 0, "context_length is a positive number");
                Equal(200, Call("GET", "/v1/models/" + FairleadModel, null, FairleadHeaders).Status, "one model by id");
            }
        }

        // Checklist: "A short non-stream request completes". Unknown headers and body fields (privacy, route, user,
        // metadata) are ignored; the usage Fairlead's Activity view reads is present.
        static void FairleadNonStream()
        {
            using (FairleadServer(0, null))
            {
                Resp r = Call("POST", "/v1/chat/completions", FairleadBody(false), FairleadHeaders);
                Equal(200, r.Status, "status (" + r.Body + ")");
                Equal("Hello from Windlass, layer by layer.", (string)Path_(r.Json, "choices", 0, "message", "content"), "content");
                Equal(FairleadModel, (string)Path_(r.Json, "model"), "the response names the model");
                Check((double)Path_(r.Json, "usage", "prompt_tokens") > 0 && (double)Path_(r.Json, "usage", "completion_tokens") > 0, "usage tokens for Fairlead's Activity view");
                Equal("stop", (string)Path_(r.Json, "choices", 0, "finish_reason"), "finish reason");
            }
        }

        // Checklist: "A streamed request survives a multi-minute layer pass (keepalives arrive)". The headers and a first
        // SSE comment arrive before any token, the stream ends with the usage chunk and [DONE], and the keepalive interval
        // is under the 60 s Fairlead's provider timeout. (The multi-minute pass itself is a hand check on a real model.)
        static void FairleadStream()
        {
            Check(new ServerSettings().KeepaliveMs < 60000, "default keepalive is under 60 s");
            using (FairleadServer(250, new ServerSettings { KeepaliveMs = 100 }))
            {
                HttpWebRequest req = (HttpWebRequest)WebRequest.Create("http://127.0.0.1:" + TestPort + "/v1/chat/completions");
                req.Method = "POST"; req.Proxy = null; req.Timeout = 20000; req.ContentType = "application/json";
                for (int i = 0; i < FairleadHeaders.Length; i += 2) req.Headers[FairleadHeaders[i]] = FairleadHeaders[i + 1];
                byte[] body = Encoding.UTF8.GetBytes(FairleadBody(true));
                using (Stream s = req.GetRequestStream()) s.Write(body, 0, body.Length);
                System.Diagnostics.Stopwatch sw = System.Diagnostics.Stopwatch.StartNew();
                using (HttpWebResponse res = (HttpWebResponse)req.GetResponse())
                {
                    Check(sw.ElapsedMilliseconds < 1000, "headers arrive as soon as the request is accepted, not after the first token (" + sw.ElapsedMilliseconds + " ms)");
                    Check(res.ContentType.StartsWith("text/event-stream"), "content type " + res.ContentType);
                    List<string> ev = ReadSse(res.GetResponseStream(), 200);
                    Equal(": accepted", ev[0], "an SSE comment first");
                    Equal("data: [DONE]", ev[ev.Count - 1], "ends with [DONE]");
                    Check(ev.Contains(": keepalive"), "keepalive comments arrive while a slow layer pass runs");
                    Dictionary<string, object> usage = null;
                    StringBuilder text = new StringBuilder();
                    foreach (string e in ev)
                    {
                        if (!e.StartsWith("data: {")) continue;
                        Dictionary<string, object> c = (Dictionary<string, object>)Json.Parse(e.Substring(6));
                        List<object> choices = (List<object>)c["choices"];
                        if (choices.Count == 0) { usage = (Dictionary<string, object>)c["usage"]; continue; }
                        Dictionary<string, object> delta = (Dictionary<string, object>)Path_(c, "choices", 0, "delta");
                        if (delta.ContainsKey("content")) text.Append((string)delta["content"]);
                    }
                    Equal("Hello from Windlass, layer by layer.", text.ToString(), "streamed text");
                    Check(usage != null && (double)usage["completion_tokens"] == 6, "final usage chunk (stream_options.include_usage)");
                }
            }
        }

        // Fairlead fails over on 5xx, 429 and timeouts and does not retry 400; errors are { error: { message, type } }.
        // Checklist: "A second request while busy gets 503 (or queues) and Fairlead reports it clearly".
        static void FairleadFailoverStatuses()
        {
            using (FairleadServer(300, new ServerSettings { MaxQueue = 0 }))
            {
                Thread a = new Thread(delegate() { Call("POST", "/v1/chat/completions", FairleadBody(false), FairleadHeaders); });
                a.Start();
                Thread.Sleep(150);
                Resp busy = Call("POST", "/v1/chat/completions", FairleadBody(false), FairleadHeaders);
                ExpectError(busy, 503, "server_busy");
                Check(((string)Path_(busy.Json, "error", "message")).Length > 0, "a message Fairlead can show");
                Equal("60", busy.Headers["Retry-After"], "Retry-After");
                a.Join();
            }
            using (FairleadServer(0, null))
            {
                ExpectError(Call("POST", "/v1/chat/completions", "{\"model\":\"some-other-model\",\"messages\":[{\"role\":\"user\",\"content\":\"x\"}]}", FairleadHeaders), 404, "not_found_error");
                ExpectError(Call("POST", "/v1/chat/completions", "{\"model\":\"" + FairleadModel + "\",\"messages\":[]}", FairleadHeaders), 400, "invalid_request_error");
            }
            StubBackend loading = new StubBackend(FairleadModel);
            loading.LoadDelayMs = 1500;
            using (new TestServer(loading, new ServerSettings(), false))
            {
                Resp r = Call("POST", "/v1/chat/completions", FairleadBody(false), FairleadHeaders);
                ExpectError(r, 503, "model_loading");
                Check(r.Headers["Retry-After"] != null, "Retry-After while loading");
            }
        }

        // Fairlead sends its client key as a Bearer token; with --api-key set on Windlass it must match.
        static void FairleadClientKey()
        {
            using (FairleadServer(0, new ServerSettings { ApiKey = FairleadKey }))
            {
                Equal(200, Call("POST", "/v1/chat/completions", FairleadBody(false), FairleadHeaders).Status, "the key Fairlead holds works");
                ExpectError(Call("POST", "/v1/chat/completions", FairleadBody(false), "Authorization", "Bearer not-the-key"), 401, "authentication_error");
            }
        }

        // Fairlead runs on the same PC and calls http://127.0.0.1:<port>/v1, or localhost; anything else is refused.
        // Windlass's port block is 8777-8779, the line after Fairlead's 8776 in edge-ops PORTS.md.
        static void FairleadAddressAndPortBlock()
        {
            using (FairleadServer(0, null))
            {
                Equal(200, Call("GET", "/v1/models", null, "Host", "127.0.0.1:" + TestPort).Status, "127.0.0.1");
                Equal(200, Call("GET", "/v1/models", null, "Host", "localhost:" + TestPort).Status, "localhost");
                Check(Call("GET", "/v1/models", null, "Host", "192.168.1.20:" + TestPort).Status >= 400, "another host name is refused (no LAN option)");
            }
            string ports = File.ReadAllText(Path.Combine(_fixtures, "..", "..", "src", "FleetPorts.psd1"));
            Check(ports.Contains("'windlass'") && ports.Contains("Start = 8777") && ports.Contains("Size = 3"), "FleetPorts.psd1 holds windlass at 8777 for 3 ports");
        }
    }
}

// Tests for the browser UI host (src\Ui.cs and the UI parts of src\Server.cs): the files it serves, the headers, the
// /api/ routes and their session token, and the rule that only a server started with --open belongs to its tab.
// C# 5 only. Real server on loopback port 18777, a stub model and a fake clock; no browser needed (the page itself is
// tested in ui-src with Vitest and Playwright).
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;

namespace Windlass.Tests
{
    public static partial class Runner
    {
        const string UiToken = "test-session-token-0123456789abcdef";

        // A ui\dist look-alike: the page, a script, a stylesheet, an icon, plus files that must never be served.
        static string MakeUiDir(bool withIndex)
        {
            string dir = Path.Combine(_temp, "ui-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Path.Combine(dir, "assets"));
            if (withIndex) File.WriteAllText(Path.Combine(dir, "index.html"), "<!doctype html><title>Windlass</title><div id=\"app\"></div>");
            File.WriteAllText(Path.Combine(dir, "assets", "app-abc123.js"), "console.log('windlass');");
            File.WriteAllText(Path.Combine(dir, "assets", "app-abc123.css"), "body{margin:0}");
            File.WriteAllBytes(Path.Combine(dir, "favicon.ico"), new byte[] { 0, 0, 1, 0 });
            File.WriteAllText(Path.Combine(dir, "assets", "tool.exe"), "MZ not a served type");
            File.WriteAllText(Path.Combine(_temp, "outside-ui.txt"), "secret outside the UI folder");
            return dir;
        }

        static ServerSettings UiSettings(string dir, bool ownsLife, FakeClock clock)
        {
            ServerSettings s = new ServerSettings { UiRoot = dir, UiOwnsLife = ownsLife, SessionToken = UiToken, PresenceTickMs = 0 };
            if (clock != null) s.Clock = clock.Get;
            return s;
        }

        static Resp ApiCall(string method, string path, string body, params string[] headers)
        {
            List<string> h = new List<string> { "X-Windlass-Session", UiToken };
            h.AddRange(headers);
            return Call(method, path, body, h.ToArray());
        }

        static void UiServesItsFiles()
        {
            string dir = MakeUiDir(true);
            using (Stub(0, UiSettings(dir, false, null)))
            {
                Resp page = Call("GET", "/", null);
                Equal(200, page.Status, "the page at /");
                Check(page.Headers["Content-Type"].StartsWith("text/html"), "html content type");
                Check(page.Body.Contains("id=\"app\""), "the page is index.html");
                Equal("no-store", page.Headers["Cache-Control"], "the page is never cached");
                Equal(UiAssets.Csp, page.Headers["Content-Security-Policy"], "the page carries the CSP");
                Check(UiAssets.Csp.Contains("default-src 'none'") && UiAssets.Csp.Contains("frame-ancestors 'none'") && !UiAssets.Csp.Contains("unsafe-inline") && !UiAssets.Csp.Contains("http"), "the CSP allows this origin only, nothing inline or remote");
                Equal("nosniff", page.Headers["X-Content-Type-Options"], "nosniff");
                Equal("no-referrer", page.Headers["Referrer-Policy"], "no referrer");
                Equal(page.Body, Call("GET", "/index.html", null).Body, "/index.html is the same page");

                Check(Call("GET", "/assets/app-abc123.js", null).Headers["Content-Type"].StartsWith("text/javascript"), "script type");
                Check(Call("GET", "/assets/app-abc123.css", null).Headers["Content-Type"].StartsWith("text/css"), "style type");
                Equal("image/x-icon", Call("GET", "/favicon.ico", null).Headers["Content-Type"], "icon type");

                Resp head = Call("HEAD", "/", null);
                Equal(200, head.Status, "HEAD answers");
                Equal("", head.Body, "HEAD has no body");

                // Only files found under the folder, only types on the list, never anything built from the request text.
                ExpectError(Call("GET", "/assets/missing.js", null), 404, "not_found_error");
                ExpectError(Call("GET", "/assets/tool.exe", null), 404, "not_found_error");
                ExpectError(Call("GET", "/../outside-ui.txt", null), 404, "not_found_error");
                ExpectError(Call("GET", "/%2e%2e/outside-ui.txt", null), 404, "not_found_error");
                ExpectError(Call("GET", "/assets/..%2f..%2foutside-ui.txt", null), 404, "not_found_error");
                ExpectError(Call("GET", "/assets/%5c..%5coutside-ui.txt", null), 404, "not_found_error");
                ExpectError(Call("POST", "/", ""), 404, "not_found_error");

                // The API and the page share one server, so the rules of the API still hold for the page.
                int forged = Call("GET", "/", null, "Host", "evil.example:" + TestPort).Status;
                Check(forged == 403 || forged == 400, "a foreign Host is refused for the page too (" + forged + ")");
                Equal(200, Call("GET", "/v1/models", null).Status, "the API is unchanged");
            }
        }

        static void UiIsOptional()
        {
            using (Stub(0, new ServerSettings()))
                ExpectError(Call("GET", "/", null), 404, "not_found_error");   // --no-ui, or no ui\dist: the API alone, as before
            string noIndex = MakeUiDir(false);
            using (Stub(0, new ServerSettings { UiRoot = noIndex }))
            {
                ExpectError(Call("GET", "/", null), 404, "not_found_error");
                ExpectError(Call("GET", "/assets/app-abc123.js", null), 404, "not_found_error");   // no index: no UI at all
                Equal(200, Call("GET", "/health", null).Status, "the API still runs");
            }
        }

        static void UiAssetsList()
        {
            string dir = MakeUiDir(true);
            UiAssets a = new UiAssets(dir);
            Check(a.Available, "index.html found");
            Equal(4, a.Count, "index, js, css, ico (the .exe is not a served type)");
            byte[] data; string type;
            Check(a.TryGet("/", out data, out type) && type.StartsWith("text/html"), "/ is index.html");
            Check(!a.TryGet("/assets", out data, out type), "a folder is not a file");
            Check(!new UiAssets(Path.Combine(_temp, "no-such-ui-folder")).Available, "a missing folder is no UI");
            Check(!new UiAssets(null).Available, "no folder is no UI");
            Equal(48, UiToken_New().Length, "24 random bytes as hex");
            Check(UiToken_New() != UiToken_New(), "tokens differ");
        }

        static string UiToken_New() { return Windlass.UiToken.New(); }

        static void UiSessionAndStatus()
        {
            string dir = MakeUiDir(true);
            using (Stub(0, UiSettings(dir, false, null)))
            {
                // No key set: the session route hands the token to anyone on this PC, like the rest of the API.
                Resp sess = Call("POST", "/api/session", "");
                Equal(200, sess.Status, "session without a key");
                Equal(UiToken, (string)sess.Json["token"], "the token");
                Equal(false, (bool)sess.Json["apiKeyRequired"], "no key needed");
                ExpectError(Call("GET", "/api/session", null), 405, "invalid_request_error");

                ExpectError(Call("GET", "/api/status", null), 401, "authentication_error");
                ExpectError(Call("GET", "/api/status", null, "X-Windlass-Session", "wrong"), 401, "authentication_error");
                ExpectError(Call("GET", "/api/status?token=" + UiToken, null), 401, "authentication_error");   // never from the query string

                Resp st = ApiCall("GET", "/api/status", null);
                Equal(200, st.Status, "status");
                Equal("no-store", st.Headers["Cache-Control"], "API answers are not cached");
                Equal("windlass.ui.status/1", (string)st.Json["schema"], "schema");
                Equal("ready", (string)st.Json["state"], "state");
                Equal("stub-model", (string)Path_(st.Json, "model", "id"), "model id");
                Equal("127.0.0.1:" + TestPort, (string)st.Json["address"], "the bound address");
                Equal(false, (bool)st.Json["busy"], "idle");
                Equal(8.0, (double)st.Json["queueMax"], "queue size");
                Equal(false, (bool)st.Json["apiKeyRequired"], "no key");
                Equal(false, (bool)Path_(st.Json, "autoStop", "ownsLife"), "the tab does not own this server");
                Equal("windlass.model/1", (string)Path_(st.Json, "windlass", "schema"), "the same windlass object as /v1/models");
                Equal(true, Path_(st.Json, "windlass", "speed", "secPerToken", "value") == null, "never measured is null, not a number");
                Check(!st.Body.Contains(UiToken), "the status never repeats the token");

                // The page may call /api/ from its own address only; another loopback port may not.
                Equal(200, ApiCall("GET", "/api/status", null, "Origin", "http://127.0.0.1:" + TestPort).Status, "own origin");
                ExpectError(ApiCall("GET", "/api/status", null, "Origin", "http://127.0.0.1:8767"), 403, "forbidden");
                ExpectError(Call("POST", "/api/session", "", "Origin", "http://localhost:9999"), 403, "forbidden");
                ExpectError(Call("POST", "/api/session", "", "Origin", "https://evil.example"), 403, "forbidden");

                Resp pb = ApiCall("GET", "/api/playbook", null);
                Equal(200, pb.Status, "playbook route");
                Equal("windlass.ui.playbook/1", (string)pb.Json["schema"], "playbook schema");
                Check(pb.Json["playbook"] == null, "a stub has no playbook");
                ExpectError(Call("GET", "/api/nothing", null, "X-Windlass-Session", UiToken), 404, "not_found_error");
            }
        }

        static void UiSessionNeedsTheApiKey()
        {
            string dir = MakeUiDir(true);
            ServerSettings s = UiSettings(dir, false, null);
            s.ApiKey = "sesame";
            using (Stub(0, s))
            {
                Equal(200, Call("GET", "/", null).Status, "the page and its files carry no secret, so they load without the key");
                ExpectError(Call("POST", "/api/session", ""), 401, "authentication_error");
                ExpectError(Call("POST", "/api/session", "", "Authorization", "Bearer nope"), 401, "authentication_error");
                Resp ok = Call("POST", "/api/session", "", "Authorization", "Bearer sesame");
                Equal(200, ok.Status, "session with the key");
                Equal(true, (bool)ok.Json["apiKeyRequired"], "the page learns a key is set");
                Equal(true, (bool)ApiCall("GET", "/api/status", null).Json["apiKeyRequired"], "status says so too");
                ExpectError(Call("GET", "/v1/models", null, "X-Windlass-Session", UiToken), 401, "authentication_error");   // the token opens /api/ only, never the model
            }
        }

        static void UiLogs()
        {
            string dir = MakeUiDir(true);
            using (Stub(0, UiSettings(dir, false, null)))
            {
                Log.Info("ui-test marker one");
                Resp all = ApiCall("GET", "/api/logs", null);
                Equal(200, all.Status, "logs");
                Equal("windlass.ui.logs/1", (string)all.Json["schema"], "schema");
                Check(all.Body.Contains("ui-test marker one"), "the line is there");
                long last = (long)(double)all.Json["last"];
                Resp none = ApiCall("GET", "/api/logs?after=" + last, null);
                Equal(0, ((List<object>)none.Json["lines"]).Count, "nothing newer than the last line");
                Log.Warn("ui-test marker two");
                Resp more = ApiCall("GET", "/api/logs?after=" + last, null);
                Equal(1, ((List<object>)more.Json["lines"]).Count, "only the new line");
                Check(more.Body.Contains("WARN") && more.Body.Contains("ui-test marker two"), "with its level");
                Equal(200, ApiCall("GET", "/api/logs?after=junk", null).Status, "a bad cursor means from the start");
                ExpectError(Call("GET", "/api/logs", null), 401, "authentication_error");
            }
            long l2;
            for (int i = 0; i < Log.KeepLines + 50; i++) Log.Info("fill " + i);
            Check(Log.Tail(0, out l2).Count <= Log.KeepLines, "the log keeps a bounded number of lines");
        }

        static void UiHeartbeatOnlyOwnsAnOpenedServer()
        {
            string dir = MakeUiDir(true);

            // Not started with --open: opening the page and closing it again must not stop a server other programs use.
            FakeClock clock = new FakeClock();
            using (TestServer ts = Stub(0, UiSettings(dir, false, clock)))
            {
                Resp hb = ApiCall("POST", "/api/heartbeat", "{\"tab\":\"a\"}");
                Equal(200, hb.Status, "heartbeat");
                Equal(false, (bool)hb.Json["ownsLife"], "it does not own the server");
                Equal(false, (bool)hb.Json["autoStop"], "so no auto-stop is promised");
                Check(!ts.Server.Presence.Armed, "and it arms nothing");
                ApiCall("POST", "/api/heartbeat", "{\"tab\":\"a\",\"leaving\":true}");
                clock.Now = T0.AddMinutes(5);
                Check(!ts.Server.PresenceTick(clock.Now), "closing the tab never stops it");
            }

            // Started with --open: the tab owns the server. Same rule as the Fleet heartbeat: 20 s of silence or 5 s after leaving.
            clock = new FakeClock();
            using (TestServer ts = Stub(0, UiSettings(dir, true, clock)))
            {
                ExpectError(Call("POST", "/api/heartbeat", "{\"tab\":\"a\"}"), 401, "authentication_error");
                ExpectError(Call("POST", "/api/heartbeat", "{\"tab\":\"a\",\"token\":\"wrong\"}"), 401, "authentication_error");
                Check(!ts.Server.Presence.Armed, "a refused heartbeat arms nothing");

                Resp hb = ApiCall("POST", "/api/heartbeat", "{\"tab\":\"a\"}");
                Equal(true, (bool)hb.Json["ownsLife"], "the tab owns it");
                Equal(true, (bool)hb.Json["autoStop"], "auto-stop is on");
                Equal(20.0, (double)hb.Json["timeoutSeconds"], "timeout");
                Check(ts.Server.Presence.Armed, "the first heartbeat arms it");

                // The page-close beacon cannot set headers: the token travels in the body, on this route only.
                Resp beacon = Call("POST", "/api/heartbeat", "{\"tab\":\"a\",\"leaving\":true,\"token\":\"" + UiToken + "\"}");
                Equal(200, beacon.Status, "beacon with the token in the body");
                clock.Now = T0.AddSeconds(4);
                Check(!ts.Server.PresenceTick(clock.Now), "inside the 5 s grace");
                ApiCall("POST", "/api/heartbeat", "{\"tab\":\"a\"}");   // a reload
                clock.Now = T0.AddSeconds(9);
                Check(!ts.Server.PresenceTick(clock.Now), "a reload inside the grace changes nothing");
                ApiCall("POST", "/api/heartbeat", "{\"tab\":\"a\",\"leaving\":true}");
                clock.Now = T0.AddSeconds(14);
                Check(ts.Server.PresenceTick(clock.Now), "5 s after the leaving signal the server stops");
                Check(ts.Server.Stopped.WaitOne(2000), "and says so");
            }

            // Two tabs: it stops only when both are gone. And --no-auto-stop keeps it, whatever the tabs do.
            clock = new FakeClock();
            using (TestServer ts = Stub(0, UiSettings(dir, true, clock)))
            {
                ApiCall("POST", "/api/heartbeat", "{\"tab\":\"a\"}");
                ApiCall("POST", "/api/heartbeat", "{\"tab\":\"b\"}");
                ApiCall("POST", "/api/heartbeat", "{\"tab\":\"a\",\"leaving\":true}");
                clock.Now = T0.AddSeconds(10);
                ApiCall("POST", "/api/heartbeat", "{\"tab\":\"b\"}");
                Check(!ts.Server.PresenceTick(clock.Now), "tab b is still there");
            }
            ServerSettings stay = UiSettings(dir, true, new FakeClock());
            stay.AutoStop = false;
            using (TestServer ts = Stub(0, stay))
            {
                Resp hb = ApiCall("POST", "/api/heartbeat", "{\"tab\":\"a\",\"leaving\":true}");
                Equal(false, (bool)hb.Json["autoStop"], "auto-stop is off");
                Check(!ts.Server.PresenceTick(T0.AddHours(1)), "--no-auto-stop keeps serving");
            }
        }

        static void UiShutdown()
        {
            string dir = MakeUiDir(true);
            using (TestServer ts = Stub(0, UiSettings(dir, false, null)))
            {
                ExpectError(Call("POST", "/api/shutdown", ""), 401, "authentication_error");
                ExpectError(Call("GET", "/api/shutdown", null, "X-Windlass-Session", UiToken), 404, "not_found_error");
                Check(!ts.Server.Stopped.WaitOne(300), "a refused call stops nothing");
                Resp r = ApiCall("POST", "/api/shutdown", "");
                Equal(200, r.Status, "shutdown accepted");
                Check(ts.Server.Stopped.WaitOne(3000), "the server stops");
            }
        }
    }
}

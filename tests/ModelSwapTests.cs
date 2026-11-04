// Tests for loading, swapping and unloading the model from the browser UI: SwitchableBackend (src\SwitchableBackend.cs), the
// file browser (src\ModelBrowser.cs) and the /api/models, /api/model and /api/model/unload routes. C# 5 only.
// A stub backend stands in for the engine, so no model file is read; the "model files" here are empty .gguf files.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Windlass.Tests
{
    public static partial class Runner
    {
        static string FakeModel(string dir, string name)
        {
            Directory.CreateDirectory(dir);
            string p = Path.Combine(dir, name);
            File.WriteAllText(p, "not a real model");
            return p;
        }

        sealed class StubFactory
        {
            public int Built;
            public int TokenDelayMs, LoadDelayMs;
            public string FailFor;               // file name that fails to load
            public Backend Make(string path)
            {
                Built++;
                if (FailFor != null && Path.GetFileName(path) == FailFor) throw new ArgumentException("simulated: " + FailFor + " does not fit");
                StubBackend b = new StubBackend(LayeredBackend.DefaultId(path));
                b.TokenDelayMs = TokenDelayMs; b.LoadDelayMs = LoadDelayMs;
                b.ContextLength = 2048;
                return b;
            }
        }

        static bool WaitUntil(Func<bool> cond, int ms)
        {
            for (int i = 0; i < ms / 20; i++) { if (cond()) return true; Thread.Sleep(20); }
            return cond();
        }

        static void SwitchableLifecycle()
        {
            string dir = Path.Combine(TempDir, "swap-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string a = FakeModel(dir, "alpha-7b.gguf"), b = FakeModel(dir, "beta-70b.gguf"), bad = FakeModel(dir, "toobig.gguf");
            StubFactory f = new StubFactory { TokenDelayMs = 150, LoadDelayMs = 100, FailFor = "toobig.gguf" };
            List<string> remembered = new List<string>();
            SwitchableBackend sw = new SwitchableBackend(f.Make, 1, null, null);
            sw.Loaded = delegate(string p) { lock (remembered) remembered.Add(Path.GetFileName(p)); };
            Equal(BackendState.Idle, sw.State, "starts idle without a model");
            Equal("", sw.ModelId, "no id while idle");
            Check(sw.ModelPath == null && sw.PlaybookJson() == null && sw.Facts().Count == 0, "no path, playbook or facts while idle");
            try { sw.Generate(new GenerationRequest(), delegate(string t) { }, null, CancellationToken.None); throw new TestFailure("idle must not generate"); }
            catch (BackendException e) { Equal(503, e.Status, "idle generate status"); }

            sw.Swap(a);
            Equal(BackendState.Loading, sw.State, "loading while the new model loads");
            Check(WaitUntil(delegate { return sw.State == BackendState.Ready; }, 5000), "the first model becomes ready");
            Equal("alpha-7b", sw.ModelId, "the id mirrors the model");
            Equal(2048, sw.ContextLength, "the context mirrors the model");
            Equal(a, sw.ModelPath, "the loaded file");
            Check(remembered.Contains("alpha-7b.gguf"), "a loaded model is reported for the recent list");

            // A swap is refused mid-reply, and mid-load; nothing changes.
            GenerationResult res = null;
            Thread gen = new Thread(delegate() { res = sw.Generate(new GenerationRequest(), delegate(string t) { }, null, CancellationToken.None); });
            gen.Start();
            Thread.Sleep(100);
            try { sw.Swap(b); throw new TestFailure("a swap mid-reply must be refused"); }
            catch (BackendException e) { Equal(409, e.Status, "mid-reply status"); Equal("server_busy", e.Kind, "mid-reply kind"); }
            Equal("alpha-7b", sw.ModelId, "the running model is untouched by the refused swap");
            gen.Join();
            Equal("stop", res.FinishReason, "the reply finished normally");

            sw.Swap(b);
            try { sw.Swap(a); throw new TestFailure("a swap while loading must be refused"); }
            catch (BackendException e) { Equal(409, e.Status, "mid-load status"); }
            Check(WaitUntil(delegate { return sw.State == BackendState.Ready; }, 5000), "the second model becomes ready");
            Equal("beta-70b", sw.ModelId, "swapped");

            // A model that fails to load leaves a readable error, and another can be loaded after it.
            sw.Swap(bad);
            Check(WaitUntil(delegate { return sw.State == BackendState.Error; }, 5000), "a failing model ends in Error");
            Check(sw.LoadError != null && sw.LoadError.Contains("does not fit"), "with the reason: " + sw.LoadError);
            Equal(bad, sw.ModelPath, "the failed file is shown");
            sw.Swap(a);
            Check(WaitUntil(delegate { return sw.State == BackendState.Ready; }, 5000), "a good model loads after a failed one");
            Check(sw.LoadError == null, "and clears the error");
            Check(!remembered.Contains("toobig.gguf"), "a failed model is not remembered");

            sw.Swap(null);
            Equal(BackendState.Idle, sw.State, "unload goes back to idle");
            Check(sw.ModelPath == null && sw.ModelId == "", "with no path and no id");
            sw.Dispose();
        }

        static void ApiModelBrowser()
        {
            string root = Path.Combine(TempDir, "browse-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string models = Path.Combine(root, "models");
            FakeModel(models, "b-model.gguf"); FakeModel(models, "A-model.GGUF"); FakeModel(models, "notes.txt"); FakeModel(Path.Combine(models, "sub"), "inner.gguf");
            string recent = Path.Combine(root, "playbooks", "recent-models.txt");
            StubFactory f = new StubFactory();
            using (TestServer ts = new TestServer(new SwitchableBackend(f.Make, 1, null, null), UiSettings(MakeUiDir(true), false, null), false))
            {
                ts.Server.GetType();
                Resp none = ApiCall("GET", "/api/models", null);
                Equal(200, none.Status, "listing with no folder");
                Equal("windlass.ui.models/1", (string)none.Json["schema"], "schema");
                Check(none.Json["dir"] == null && none.Json["parent"] == null, "no folder: no dir and no parent");
                Check(none.Json["entries"] is List<object> && none.Json["recent"] is List<object>, "drives and recent are lists");

                Resp ls = ApiCall("GET", "/api/models?dir=" + Uri.EscapeDataString(models), null);
                Equal(200, ls.Status, "listing a folder");
                List<object> entries = (List<object>)ls.Json["entries"];
                Equal(3, entries.Count, "the sub folder and the two .gguf files; not notes.txt");
                Equal("dir", (string)Path_(ls.Json, "entries", 0, "type"), "folders first");
                Equal("sub", (string)Path_(ls.Json, "entries", 0, "name"), "the folder");
                Equal("A-model.GGUF", (string)Path_(ls.Json, "entries", 1, "name"), "files sorted without regard to case");
                Equal("b-model.gguf", (string)Path_(ls.Json, "entries", 2, "name"), "then b");
                Check((double)Path_(ls.Json, "entries", 2, "bytes") > 0, "files carry their size");
                Check(Path_(ls.Json, "entries", 0, "bytes") == null, "folders have none");
                Equal(Path.GetFullPath(root), (string)ls.Json["parent"], "the parent folder, for Up");

                ExpectError(ApiCall("GET", "/api/models?dir=" + Uri.EscapeDataString(Path.Combine(root, "nope")), null), 404, "not_found_error");
                ExpectError(ApiCall("GET", "/api/models?dir=" + Uri.EscapeDataString("\\\\server\\share"), null), 400, "invalid_request_error");
                ExpectError(ApiCall("GET", "/api/models?dir=" + Uri.EscapeDataString("//server/share"), null), 400, "invalid_request_error");
                ExpectError(Call("GET", "/api/models", null), 401, "authentication_error");
            }
            Check(!File.Exists(recent), "browsing writes nothing");
        }

        static void ApiModelSwap()
        {
            string root = Path.Combine(TempDir, "api-swap-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string a = FakeModel(Path.Combine(root, "m"), "alpha-7b.gguf"), b = FakeModel(Path.Combine(root, "m"), "beta-70b.gguf");
            string txt = FakeModel(Path.Combine(root, "m"), "readme.txt");
            string recent = Path.Combine(root, "playbooks", "recent-models.txt");
            StubFactory f = new StubFactory { TokenDelayMs = 300, LoadDelayMs = 150 };
            ServerSettings s = UiSettings(MakeUiDir(true), false, null);
            s.RecentFile = recent;
            using (TestServer ts = new TestServer(new SwitchableBackend(f.Make, 1, null, null), s, false))
            {
                // Idle: the API says so everywhere, and chat is a clear 503.
                Resp st = ApiCall("GET", "/api/status", null);
                Equal("idle", (string)st.Json["state"], "status: idle");
                Check(Path_(st.Json, "model", "id") == null && Path_(st.Json, "model", "path") == null, "status: no model id or path");
                Equal(true, (bool)st.Json["canSwap"], "status: this server can swap");
                Equal("idle", (string)Call("GET", "/health", null).Json["status"], "health: idle");
                Equal(0, ((List<object>)Call("GET", "/v1/models", null).Json["data"]).Count, "/v1/models lists nothing while idle");
                ExpectError(Call("GET", "/v1/models/anything", null), 404, "not_found_error");
                ExpectError(Call("POST", "/v1/chat/completions", "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"), 503, "no_model");

                // Bad requests.
                ExpectError(Call("POST", "/api/model", "{\"path\":\"" + a.Replace("\\", "\\\\") + "\"}"), 401, "authentication_error");
                ExpectError(ApiCall("POST", "/api/model", "{}"), 400, "invalid_request_error");
                ExpectError(ApiCall("POST", "/api/model", "{\"path\":\"" + txt.Replace("\\", "\\\\") + "\"}"), 400, "invalid_request_error");
                ExpectError(ApiCall("POST", "/api/model", "{\"path\":\"" + Path.Combine(root, "m", "gone.gguf").Replace("\\", "\\\\") + "\"}"), 404, "not_found_error");
                ExpectError(ApiCall("POST", "/api/model", "{\"path\":\"\\\\\\\\server\\\\share\\\\x.gguf\"}"), 400, "invalid_request_error");
                ExpectError(ApiCall("GET", "/api/model", null), 404, "not_found_error");
                Equal(0, f.Built, "a refused request builds nothing");

                // Load the first model.
                Resp load = ApiCall("POST", "/api/model", "{\"path\":\"" + a.Replace("\\", "\\\\") + "\"}");
                Equal(202, load.Status, "load accepted (" + load.Body + ")");
                Equal("alpha-7b", (string)load.Json["id"], "id from the file name");
                Check(WaitUntil(delegate { return (string)ApiCall("GET", "/api/status", null).Json["state"] == "ready"; }, 5000), "it becomes ready");
                st = ApiCall("GET", "/api/status", null);
                Equal("alpha-7b", (string)Path_(st.Json, "model", "id"), "status: the id");
                Equal(Path.GetFullPath(a), (string)Path_(st.Json, "model", "path"), "status: the file");
                Equal(2048.0, (double)Path_(st.Json, "model", "contextLength"), "status: the context");
                Equal("alpha-7b", (string)Path_(Call("GET", "/v1/models", null).Json, "data", 0, "id"), "/v1/models lists it");
                Equal(200, Call("POST", "/v1/chat/completions", "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}").Status, "chat works");
                Check(File.Exists(recent) && File.ReadAllText(recent).Contains("alpha-7b.gguf"), "the loaded model is remembered");
                Resp listed = ApiCall("GET", "/api/models", null);
                Equal("alpha-7b.gguf", (string)Path_(listed.Json, "recent", 0, "name"), "and offered as recent");
                Equal(true, (bool)Path_(listed.Json, "recent", 0, "exists"), "with its existence checked");

                // A swap is refused while a reply is running; the running model carries on.
                string reply = null;
                Thread gen = new Thread(delegate() { reply = Call("POST", "/v1/chat/completions", "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}").Body; });
                gen.Start();
                Thread.Sleep(250);
                ExpectError(ApiCall("POST", "/api/model", "{\"path\":\"" + b.Replace("\\", "\\\\") + "\"}"), 409, "server_busy");
                ExpectError(ApiCall("POST", "/api/model/unload", ""), 409, "server_busy");
                gen.Join();
                Check(reply != null && reply.Contains("Hello from Windlass"), "the reply that was running finished");
                Equal("alpha-7b", (string)Path_(ApiCall("GET", "/api/status", null).Json, "model", "id"), "still the first model");

                // Swap: loading shows as a state, requests get 503 model_loading, then the new model answers.
                Equal(202, ApiCall("POST", "/api/model", "{\"path\":\"" + b.Replace("\\", "\\\\") + "\"}").Status, "swap accepted");
                Equal("loading", (string)ApiCall("GET", "/api/status", null).Json["state"], "loading during the swap");
                ExpectError(Call("POST", "/v1/chat/completions", "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"), 503, "model_loading");
                ExpectError(ApiCall("POST", "/api/model", "{\"path\":\"" + a.Replace("\\", "\\\\") + "\"}"), 409, "server_busy");
                Check(WaitUntil(delegate { return (string)ApiCall("GET", "/api/status", null).Json["state"] == "ready"; }, 5000), "the second model becomes ready");
                Equal("beta-70b", (string)Call("GET", "/health", null).Json["model"], "health shows the new model");
                Equal("beta-70b.gguf", (string)Path_(ApiCall("GET", "/api/models", null).Json, "recent", 0, "name"), "newest first in the recent list");
                Equal("alpha-7b.gguf", (string)Path_(ApiCall("GET", "/api/models", null).Json, "recent", 1, "name"), "the first is second");

                // Unload.
                Equal(200, ApiCall("POST", "/api/model/unload", "").Status, "unload");
                Equal("idle", (string)ApiCall("GET", "/api/status", null).Json["state"], "idle again");
                ExpectError(Call("POST", "/v1/chat/completions", "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"), 503, "no_model");
            }

            // A stub server cannot swap.
            using (Stub(0, UiSettings(MakeUiDir(true), false, null)))
            {
                Equal(false, (bool)ApiCall("GET", "/api/status", null).Json["canSwap"], "a stub server says it cannot swap");
                ExpectError(ApiCall("POST", "/api/model", "{\"path\":\"" + a.Replace("\\", "\\\\") + "\"}"), 400, "invalid_request_error");
                ExpectError(ApiCall("POST", "/api/model/unload", ""), 400, "invalid_request_error");
            }
        }

        static void ApiModelFailureIsShown()
        {
            string root = Path.Combine(TempDir, "api-fail-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            string bad = FakeModel(Path.Combine(root, "m"), "toobig.gguf"), good = FakeModel(Path.Combine(root, "m"), "small.gguf");
            StubFactory f = new StubFactory { FailFor = "toobig.gguf" };
            using (TestServer ts = new TestServer(new SwitchableBackend(f.Make, 1, null, null), UiSettings(MakeUiDir(true), false, null), false))
            {
                Equal(202, ApiCall("POST", "/api/model", "{\"path\":\"" + bad.Replace("\\", "\\\\") + "\"}").Status, "accepted; the failure comes later");
                Check(WaitUntil(delegate { return (string)ApiCall("GET", "/api/status", null).Json["state"] == "error"; }, 5000), "the load fails");
                Resp st = ApiCall("GET", "/api/status", null);
                Check(((string)st.Json["loadError"]).Contains("does not fit"), "the reason is in the status");
                ExpectError(Call("POST", "/v1/chat/completions", "{\"messages\":[{\"role\":\"user\",\"content\":\"hi\"}]}"), 503, "model_load_failed");
                Equal(202, ApiCall("POST", "/api/model", "{\"path\":\"" + good.Replace("\\", "\\\\") + "\"}").Status, "a good model can be loaded after a failure");
                Check(WaitUntil(delegate { return (string)ApiCall("GET", "/api/status", null).Json["state"] == "ready"; }, 5000), "and it works");
            }
        }

        static void ServeNeedsAModelWithoutUi()
        {
            TextWriter old = Console.Error;
            StringWriter sw = new StringWriter();
            int rc;
            try { Console.SetError(sw); rc = Program.Run(new string[] { "serve", "--no-ui", "--port", "18779" }, TempDir, null, "0"); }
            finally { Console.SetError(old); }
            Equal(1, rc, "serve with no model and no UI is an error");
        }

        // The real engine: two tiny GGUF models, loaded and swapped through the same factory shape Program.Serve uses.
        static void ApiRealModelSwap()
        {
            string m1 = Model(new TinySpec { Arch = "qwen3", Type = GgmlType.Q4_K });
            string m2 = Model(new TinySpec { Arch = "llama", Type = GgmlType.Q8_0 });
            Func<string, Backend> factory = delegate(string file)
            {
                Dictionary<string, string> o = new Dictionary<string, string>();
                o["model"] = file;
                return new LayeredBackend(Program.Engine(o, null));
            };
            string body = "{\"messages\":[{\"role\":\"user\",\"content\":\"Hello world\"}],\"max_tokens\":4,\"temperature\":0}";
            using (TestServer ts = new TestServer(new SwitchableBackend(factory, 1, null, null), UiSettings(MakeUiDir(true), false, null), false))
            {
                Equal(202, ApiCall("POST", "/api/model", "{\"path\":\"" + m1.Replace("\\", "\\\\") + "\"}").Status, "load the first model");
                Check(WaitUntil(delegate { return (string)ApiCall("GET", "/api/status", null).Json["state"] == "ready"; }, 20000), "the first model loads");
                Resp st = ApiCall("GET", "/api/status", null);
                Equal(LayeredBackend.DefaultId(m1), (string)Path_(st.Json, "model", "id"), "its id is the file name");
                Equal("qwen3", (string)Path_(st.Json, "windlass", "architecture"), "the facts are the loaded model's");
                Resp r1 = Call("POST", "/v1/chat/completions", body);
                Equal(200, r1.Status, "the first model answers (" + r1.Body + ")");
                Equal(4.0, (double)Path_(r1.Json, "usage", "completion_tokens"), "four tokens");

                Equal(202, ApiCall("POST", "/api/model", "{\"path\":\"" + m2.Replace("\\", "\\\\") + "\"}").Status, "swap to the second model");
                Check(WaitUntil(delegate { return (string)ApiCall("GET", "/api/status", null).Json["state"] == "ready"; }, 20000), "the second model loads");
                st = ApiCall("GET", "/api/status", null);
                Equal(LayeredBackend.DefaultId(m2), (string)Path_(st.Json, "model", "id"), "the id follows the swap");
                Equal("llama", (string)Path_(st.Json, "windlass", "architecture"), "so do the facts");
                Equal(200, Call("POST", "/v1/chat/completions", body).Status, "the second model answers");
                File.Delete(m1);   // the first model's file is released (on Windows an open handle would make this throw)

                Equal(200, ApiCall("POST", "/api/model/unload", "").Status, "unload");
                File.Delete(m2);
            }
        }
    }
}

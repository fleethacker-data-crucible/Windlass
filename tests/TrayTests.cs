// Tests for the notification-area icon's engine side (src\TrayHost.cs, Program.UiOwnsServer): the engine runs on a
// background thread, keeps serving when the browser's tab is gone (--tray), reports its status, and Quit stops it and
// closes the model. The icon itself (src\Tray.ps1, WinForms) needs a desktop session and is checked by hand; its
// decisions are tested in Windlass.Tests.ps1. C# 5 only.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Threading;

namespace Windlass.Tests
{
    public static partial class Runner
    {
        static string TrayRoot()
        {
            string root = Path.Combine(_temp, "tray-root-" + Guid.NewGuid().ToString("N").Substring(0, 6));
            Directory.CreateDirectory(Path.Combine(root, "ui", "dist"));
            File.WriteAllText(Path.Combine(root, "ui", "dist", "index.html"), "<!doctype html><title>Windlass</title>");
            return root;
        }

        static bool TrayWait(Func<bool> cond, int ms)
        {
            for (int i = 0; i < ms / 20 && !cond(); i++) Thread.Sleep(20);
            return cond();
        }

        // The engine as the icon runs it; returns the session token for /api/*.
        static string TrayStart(string root, params string[] args)
        {
            TrayHost.Start(args, root, "", "test");
            Check(TrayWait(delegate() { return TrayHost.Listening; }, 15000), "the engine answers: " + string.Join(" ", args));
            Resp s = Call("POST", "/api/session", "");
            Equal(200, s.Status, "session");
            return (string)s.Json["token"];
        }

        static void TrayOwnership()
        {
            Dictionary<string, string> open = Program.ParseArgs(new string[] { "--open" }, 0);
            Dictionary<string, string> tray = Program.ParseArgs(new string[] { "--open", "--tray" }, 0);
            Dictionary<string, string> none = Program.ParseArgs(new string[] { "--tray" }, 0);
            Dictionary<string, string> off = Program.ParseArgs(new string[] { "--open", "--no-tray" }, 0);
            Check(Program.UiOwnsServer(open, true), "--open alone: the tab owns the server, as before");
            Check(Program.UiOwnsServer(off, true), "--open --no-tray: the tab owns it");
            Check(!Program.UiOwnsServer(tray, true), "--open --tray: the icon holds the server, the tab does not own it");
            Check(!Program.UiOwnsServer(none, true), "--tray without --open: nothing owns it");
            Check(!Program.UiOwnsServer(open, false), "no UI: nothing to own it");
            Check(TrayHost.Quit(1000), "Quit with nothing running is harmless");
            Equal("Windlass has stopped", TrayHost.StatusLine(), "status before any start");
        }

        static void TrayEngineOutlivesTheBrowser()
        {
            string root = TrayRoot();
            string logs = Path.Combine(root, "logs");

            // With the icon: the tab's heartbeats and leaving signal do not stop the engine; the model stays loaded.
            string token = TrayStart(root, "serve", "--stub", "--open", "--tray", "--port", TestPort.ToString());
            Check(TrayHost.Running, "the engine thread is running");
            Equal(TestPort, TrayHost.Port, "port");
            Check(TrayWait(delegate() { return TrayHost.StatusLine() == "Running on 127.0.0.1:" + TestPort + ", model: windlass-stub"; }, 5000), "status line names the address and model: " + TrayHost.StatusLine());
            Resp st = Call("GET", "/api/status", null, "X-Windlass-Session", token);
            Equal(false, (bool)Path_(st.Json, "autoStop", "ownsLife"), "the tab does not own this server");
            Call("POST", "/api/heartbeat", "{\"tab\":\"a\"}", "X-Windlass-Session", token);
            Call("POST", "/api/heartbeat", "{\"tab\":\"a\",\"leaving\":true}", "X-Windlass-Session", token);
            Check(!Program.LiveServer.Presence.Armed, "a closed tab arms nothing");
            Check(!Program.LiveServer.PresenceTick(DateTime.UtcNow.AddHours(1)), "an hour after the last tab left, the engine is still up");
            Check(TrayHost.Running && TrayHost.Listening, "still running");
            Check(Call("GET", "/v1/models", null).Body.Contains("windlass-stub"), "the model list still names the loaded model");
            Equal(200, Call("GET", "/__fleethacker/id", null).Status, "and answers programs such as Fairlead");

            // Quit: the server stops, the model is closed, the runtime file goes, the thread ends with 0.
            Check(Directory.GetFiles(logs, "windlass-server-*.json").Length == 1, "a runtime file while it runs");
            Check(TrayHost.Quit(15000), "Quit ends the engine thread");
            Equal(0, TrayHost.ExitCode, "exit code");
            Check(!TrayHost.Running && !TrayHost.Listening, "stopped");
            Equal(0, Directory.GetFiles(logs, "windlass-server-*.json").Length, "the runtime file is removed");
            try { Call("GET", "/__fleethacker/id", null); throw new TestFailure("the port should be closed after Quit"); }
            catch (WebException) { }

            // Without the icon (--open alone) the tab owns the server, as before.
            token = TrayStart(root, "serve", "--stub", "--open", "--port", TestPort.ToString());
            st = Call("GET", "/api/status", null, "X-Windlass-Session", token);
            Equal(true, (bool)Path_(st.Json, "autoStop", "ownsLife"), "without --tray the tab owns the server");
            Check(TrayHost.Quit(15000), "Quit again");

            // A start that fails (no model, no UI) ends the thread with the engine's exit code, so the icon removes itself.
            TrayHost.Start(new string[] { "serve", "--no-ui", "--port", TestPort.ToString() }, root, "", "test");
            Check(TrayWait(delegate() { return !TrayHost.Running; }, 10000), "a failed start ends the thread");
            Equal(1, TrayHost.ExitCode, "and reports the failure");
        }
    }
}

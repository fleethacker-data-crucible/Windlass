// The engine side of the notification-area icon (Tray.ps1 draws the icon; this file holds what it asks of the engine, so
// the start, status and quit logic runs without a screen and the tests can drive it). C# 5 only.
// The icon runs the server on a background thread and keeps the PowerShell thread for the Windows message loop.
using System;
using System.Threading;

namespace Windlass
{
    public static class TrayHost
    {
        static Thread _thread;
        static int _exit = -1;

        // Runs Program.Run (a serve command) on its own thread and returns at once.
        public static void Start(string[] args, string root, string scriptPath, string version)
        {
            if (_thread != null && _thread.IsAlive) throw new InvalidOperationException("The engine is already running.");
            _exit = -1;
            _thread = new Thread(delegate() { _exit = Program.Run(args, root, scriptPath, version); });
            _thread.Name = "windlass-engine";
            _thread.IsBackground = false;
            _thread.Start();
        }

        // True while the engine thread runs (the icon stays while this is true).
        public static bool Running { get { Thread t = _thread; return t != null && t.IsAlive; } }

        // The engine's exit code once it has stopped, else -1.
        public static int ExitCode { get { return Running ? -1 : _exit; } }

        // True once the server answers (the icon's menu can act); false before it starts and after it stops.
        public static bool Listening { get { return Program.LiveServer != null && !Program.LiveServer.Stopped.WaitOne(0); } }

        public static int Port { get { return Program.LivePort; } }

        // The model's name, "loading <name>" while one loads, or "none".
        public static string ModelText()
        {
            Backend b = Program.LiveBackend;
            if (b == null) return "none";
            switch (b.State)
            {
                case BackendState.Ready: return string.IsNullOrEmpty(b.ModelId) ? "loaded" : b.ModelId;
                case BackendState.Loading: return string.IsNullOrEmpty(b.ModelId) ? "loading" : "loading " + b.ModelId;
                case BackendState.Error: return "load failed";
                default: return "none";
            }
        }

        // For the menu's disabled status line and the tooltip.
        public static string StatusLine()
        {
            if (!Listening) return Running ? "Windlass is starting" : "Windlass has stopped";
            return "Running on 127.0.0.1:" + Port + ", model: " + ModelText();
        }

        // Stops the server (what "Quit Windlass" does): the thread then closes the model and removes its runtime file.
        // Returns true when the engine thread has ended within waitMs.
        public static bool Quit(int waitMs)
        {
            ApiServer s = Program.LiveServer;
            for (int i = 0; s == null && Running && i < 100; i++) { Thread.Sleep(50); s = Program.LiveServer; }   // still starting
            if (s != null) s.Stop();
            Thread t = _thread;
            return t == null || t.Join(waitMs);
        }
    }
}

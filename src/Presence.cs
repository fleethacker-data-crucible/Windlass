// Presence rules for the Fleet local app lifecycle standard (Fairlead docs/LOCAL_APP_LIFECYCLE.md). C# 5 only.
// A program that owns Windlass's life (a launcher, a browser page, Fairlead) sends a heartbeat now and then and a
// "leaving" signal when it goes. With no heartbeat for TimeoutSeconds, or LeavingGraceSeconds after a leaving signal with
// no new heartbeat, Windlass stops itself. It never stops mid-generation (a running or queued request holds it open)
// and it stays alone until the first heartbeat arrives, so clients that never send one keep today's behaviour.
// The clock is passed in, so tests use a fake one.
using System;
using System.Collections.Generic;

namespace Windlass
{
    public sealed class Presence
    {
        public const int TimeoutSeconds = 20;
        public const int LeavingGraceSeconds = 5;

        sealed class Client
        {
            public DateTime Last;
            public DateTime? Leaving;
        }

        readonly object _gate = new object();
        readonly Dictionary<string, Client> _clients = new Dictionary<string, Client>();
        bool _armed;

        public readonly bool Enabled;

        public Presence(bool enabled) { Enabled = enabled; }

        // True once any heartbeat has arrived: from then on silence stops the server.
        public bool Armed { get { lock (_gate) return _armed; } }

        public int Clients { get { lock (_gate) return _clients.Count; } }

        // A heartbeat (leaving = false) or a leaving signal from one client; client "" is the anonymous one.
        public void Heartbeat(string client, bool leaving, DateTime now)
        {
            if (client == null) client = "";
            lock (_gate)
            {
                Client c;
                if (!_clients.TryGetValue(client, out c)) { c = new Client(); c.Last = now; _clients[client] = c; }
                _armed = true;
                if (leaving) { if (c.Leaving == null) c.Leaving = now; }
                else { c.Last = now; c.Leaving = null; }
            }
        }

        // True when every client that ever reported is gone and nothing is running. busy = a request is running or
        // queued: the stop waits, and happens on the first tick after it finishes.
        public bool ShouldStop(DateTime now, bool busy)
        {
            lock (_gate)
            {
                if (!Enabled || !_armed || _clients.Count == 0) return false;
                List<string> gone = new List<string>();
                foreach (KeyValuePair<string, Client> kv in _clients)
                {
                    Client c = kv.Value;
                    bool left = c.Leaving.HasValue && (now - c.Leaving.Value).TotalSeconds >= LeavingGraceSeconds;
                    bool silent = !c.Leaving.HasValue && (now - c.Last).TotalSeconds >= TimeoutSeconds;
                    if (left || silent) gone.Add(kv.Key);
                }
                if (gone.Count < _clients.Count) { foreach (string g in gone) _clients.Remove(g); return false; }
                return !busy;
            }
        }
    }
}

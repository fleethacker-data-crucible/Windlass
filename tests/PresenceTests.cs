// Tests for the heartbeat lifecycle rule (src\Presence.cs and POST /__fleethacker/heartbeat). C# 5 only.
// A fake clock drives every timing, so nothing here waits: the "20 seconds of silence" take no real time.
using System;
using System.Collections.Generic;
using System.Threading;

namespace Windlass.Tests
{
    public static partial class Runner
    {
        static readonly DateTime T0 = new DateTime(2026, 10, 31, 12, 0, 0, DateTimeKind.Utc);

        static void PresenceRules()
        {
            // Off, or never armed: never stops, however long it is quiet.
            Presence off = new Presence(false);
            off.Heartbeat("a", false, T0);
            Check(!off.ShouldStop(T0.AddHours(5), false), "a disabled rule never stops");
            Presence unarmed = new Presence(true);
            Check(!unarmed.Armed && !unarmed.ShouldStop(T0.AddHours(5), false), "no heartbeat yet: clients that never send one keep running");

            // Armed: stops after 20 s of silence, not before.
            Presence p = new Presence(true);
            p.Heartbeat("tab", false, T0);
            Check(p.Armed && p.Clients == 1, "the first heartbeat arms it");
            Check(!p.ShouldStop(T0.AddSeconds(19.9), false), "19.9 s of silence is still alive");
            Check(p.ShouldStop(T0.AddSeconds(20), false), "20 s of silence stops");

            // Regular heartbeats keep it alive.
            p = new Presence(true);
            for (int s = 0; s <= 60; s += 5) { p.Heartbeat("tab", false, T0.AddSeconds(s)); Check(!p.ShouldStop(T0.AddSeconds(s + 4), false), "alive at " + s); }

            // A leaving signal stops it 5 s later; a reload (fresh heartbeat inside the grace) does not.
            p = new Presence(true);
            p.Heartbeat("tab", false, T0);
            p.Heartbeat("tab", true, T0.AddSeconds(1));
            Check(!p.ShouldStop(T0.AddSeconds(5.9), false), "inside the 5 s grace after leaving");
            Check(p.ShouldStop(T0.AddSeconds(6), false), "5 s after leaving, with no new heartbeat: stop");
            p = new Presence(true);
            p.Heartbeat("tab", false, T0);
            p.Heartbeat("tab", true, T0.AddSeconds(1));
            p.Heartbeat("tab", false, T0.AddSeconds(3));
            Check(!p.ShouldStop(T0.AddSeconds(8), false), "a reload's fresh heartbeat cancels the leaving signal");
            Check(p.ShouldStop(T0.AddSeconds(23), false), "and silence after it still stops (20 s from the last heartbeat)");

            // A leaving signal from a client never seen before still counts, 5 s later.
            p = new Presence(true);
            p.Heartbeat("ghost", true, T0);
            Check(!p.ShouldStop(T0.AddSeconds(4), false) && p.ShouldStop(T0.AddSeconds(5), false), "an unknown client that leaves stops it after the grace");

            // Several clients: it stops only when every one is gone.
            p = new Presence(true);
            p.Heartbeat("a", false, T0);
            p.Heartbeat("b", false, T0);
            p.Heartbeat("a", true, T0.AddSeconds(2));
            Check(!p.ShouldStop(T0.AddSeconds(10), false), "b keeps it alive after a leaves");
            Equal(1, p.Clients, "a is forgotten once gone");
            p.Heartbeat("b", false, T0.AddSeconds(10));
            Check(!p.ShouldStop(T0.AddSeconds(29), false), "b's last heartbeat at 10 s keeps it to 30 s");
            Check(p.ShouldStop(T0.AddSeconds(30), false), "b goes quiet: stop");

            // Never mid-generation: a running or queued request holds it, and it stops on the first tick after.
            p = new Presence(true);
            p.Heartbeat("tab", false, T0);
            Check(!p.ShouldStop(T0.AddSeconds(300), true), "busy: do not stop, however long the silence");
            Check(!p.ShouldStop(T0.AddSeconds(301), true), "still busy");
            Check(p.ShouldStop(T0.AddSeconds(302), false), "the first tick after the request ends stops it");
            p = new Presence(true);
            p.Heartbeat("tab", false, T0);
            p.Heartbeat("tab", false, T0.AddSeconds(300));
            Check(!p.ShouldStop(T0.AddSeconds(302), false), "a client that came back while busy keeps it alive");

            // Anonymous heartbeats (no client name) share one slot.
            p = new Presence(true);
            p.Heartbeat(null, false, T0);
            p.Heartbeat("", false, T0.AddSeconds(10));
            Equal(1, p.Clients, "null and empty client names are the same anonymous client");
        }

        sealed class FakeClock
        {
            public DateTime Now = T0;
            public DateTime Get() { return Now; }
        }

        static Resp Heartbeat(string body, bool header)
        {
            return header ? Call("POST", "/__fleethacker/heartbeat", body, "X-FleetHacker-Heartbeat", "1")
                          : Call("POST", "/__fleethacker/heartbeat", body);
        }

        static void ApiHeartbeat()
        {
            // The route needs its header, answers with the rule, and arms auto-stop.
            FakeClock clock = new FakeClock();
            using (TestServer ts = Stub(0, new ServerSettings { Clock = clock.Get, PresenceTickMs = 0 }))
            {
                ExpectError(Heartbeat("{}", false), 400, "invalid_request_error");
                Check(!ts.Server.Presence.Armed, "a refused heartbeat does not arm it");
                Check(!ts.Server.PresenceTick(clock.Now.AddHours(2)), "never armed: no stop");

                Resp ok = Heartbeat("{\"client\":\"fairlead\"}", true);
                Equal(200, ok.Status, "heartbeat status");
                Equal(true, (bool)Path_(ok.Json, "ok"), "ok");
                Equal(true, (bool)Path_(ok.Json, "autoStop"), "auto-stop is on");
                Equal(20.0, (double)Path_(ok.Json, "timeoutSeconds"), "timeout");
                Equal(5.0, (double)Path_(ok.Json, "leavingGraceSeconds"), "grace");
                Check(ts.Server.Presence.Armed, "a heartbeat arms it");
                Equal(200, Heartbeat("", true).Status, "a heartbeat with an empty body is fine (anonymous client)");
                Equal(200, Heartbeat("not json", true).Status, "a body that is not JSON is ignored, not an error");

                clock.Now = T0.AddSeconds(10);
                Check(!ts.Server.PresenceTick(clock.Now), "10 s of silence: alive");
                Check(!ts.Server.Stopped.WaitOne(0), "and not stopped");
                clock.Now = T0.AddSeconds(21);
                Check(ts.Server.PresenceTick(clock.Now), "21 s of silence: the server stops itself");
                Check(ts.Server.Stopped.WaitOne(2000), "and signals that it stopped");
            }

            // A leaving signal over HTTP, then the grace.
            clock = new FakeClock();
            using (TestServer ts = Stub(0, new ServerSettings { Clock = clock.Get, PresenceTickMs = 0 }))
            {
                Heartbeat("{\"client\":\"tab\"}", true);
                Heartbeat("{\"client\":\"tab\",\"leaving\":true}", true);
                clock.Now = T0.AddSeconds(4);
                Check(!ts.Server.PresenceTick(clock.Now), "inside the grace");
                Heartbeat("{\"client\":\"tab\"}", true);   // the page reloaded
                clock.Now = T0.AddSeconds(9);
                Check(!ts.Server.PresenceTick(clock.Now), "a reload inside the grace changes nothing");
                Heartbeat("{\"client\":\"tab\",\"leaving\":true}", true);
                clock.Now = T0.AddSeconds(14);
                Check(ts.Server.PresenceTick(clock.Now), "5 s after the last leaving signal: stop");
            }

            // Never mid-generation: a request in flight holds the stop; the first tick after it ends stops.
            clock = new FakeClock();
            using (TestServer ts = Stub(150, new ServerSettings { Clock = clock.Get, PresenceTickMs = 0 }))
            {
                Heartbeat("{\"client\":\"tab\"}", true);
                clock.Now = T0.AddMinutes(10);
                string reply = null;
                Thread t = new Thread(delegate() { reply = Call("POST", "/v1/chat/completions", Hi + "}").Body; });
                t.Start();
                bool sawBusy = false;
                for (int i = 0; i < 100 && !sawBusy; i++)
                {
                    Thread.Sleep(10);
                    if (Call("GET", "/health", null).Body.Contains("\"busy\":true")) sawBusy = true;
                }
                Check(sawBusy, "the slow request is running");
                Check(!ts.Server.PresenceTick(clock.Now), "silent for 10 minutes but a request is running: do not stop");
                t.Join(10000);
                Check(reply != null && reply.Contains("Hello from Windlass"), "the request finished normally");
                Check(ts.Server.PresenceTick(clock.Now), "the first tick after it finished stops the server");
            }

            // The off switch: heartbeats are accepted, nothing ever stops.
            clock = new FakeClock();
            using (TestServer ts = Stub(0, new ServerSettings { Clock = clock.Get, PresenceTickMs = 0, AutoStop = false }))
            {
                Resp ok = Heartbeat("{\"client\":\"tab\"}", true);
                Equal(false, (bool)Path_(ok.Json, "autoStop"), "auto-stop reported off");
                Check(!ts.Server.PresenceTick(T0.AddDays(1)), "auto-stop off: never stops");
            }
        }
    }
}

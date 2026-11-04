// A backend whose model can be loaded, swapped and unloaded while the server runs (the browser UI's model picker).
// C# 5 only, ASCII only. It wraps one real backend at a time and mirrors its id, context and state for the server, which
// reads those fields everywhere. A swap is refused while a reply is running (never mid-reply); the old model is
// disposed first, so two big models never sit in memory together, and the new one loads on its own thread while the
// server answers 503 model_loading.
using System;
using System.Collections.Generic;
using System.Threading;

namespace Windlass
{
    public sealed class SwitchableBackend : Backend
    {
        readonly Func<string, Backend> _factory;   // file -> a new, unloaded backend (this server's options + that model's playbook)
        readonly int _slots;
        readonly object _gate = new object();
        Backend _inner;
        int _active;                                // Generate calls in flight

        // Called (on the loading thread) after a model has loaded, with its file. The server remembers it as a recent model.
        public Action<string> Loaded;

        // The file of the model that is loaded, loading or failed to load; null when none.
        public string ModelPath { get; private set; }

        // initial: a backend built from --model (loaded by the server's start-up thread through Load), or null to start idle.
        public SwitchableBackend(Func<string, Backend> factory, int slots, Backend initial, string initialPath)
        {
            _factory = factory;
            _slots = Math.Max(1, slots);
            if (initial != null)
            {
                _inner = initial;
                ModelPath = initialPath;
                ModelId = initial.ModelId;
                State = BackendState.Loading;
            }
            else
            {
                ModelId = "";
                ContextLength = 0;
                State = BackendState.Idle;
            }
        }

        // The batch size is fixed for the life of the server: its turns are granted from a scheduler built at start.
        public override int MaxConcurrent { get { return _slots; } }

        // The initial model's load. A failure is recorded by the server (LoadError, State = Error).
        public override void Load()
        {
            Backend b;
            lock (_gate) { b = _inner; }
            if (b == null) return;
            b.Load();
            Finish(b);
        }

        void Finish(Backend b)
        {
            string path;
            lock (_gate)
            {
                if (_inner != b) return;
                ContextLength = b.ContextLength;
                LoadError = null;
                State = BackendState.Ready;
                path = ModelPath;
            }
            Action<string> cb = Loaded;
            if (cb != null && path != null) { try { cb(path); } catch (Exception e) { Log.Warn("could not remember the model: " + e.Message); } }
        }

        // Loads another model (path), or unloads (null). Throws BackendException 409 while a reply is running or a model
        // is loading. Returns at once: the load runs on its own thread; watch State.
        public void Swap(string path)
        {
            Backend old;
            lock (_gate)
            {
                if (State == BackendState.Loading) throw new BackendException("A model is loading; wait until it is ready or failed", 409, "server_busy");
                if (_active > 0) throw new BackendException("Windlass is writing a reply; wait for it to finish, then change the model", 409, "server_busy");
                old = _inner;
                _inner = null;
                ModelPath = null;
                ModelId = "";
                ContextLength = 0;
                LoadError = null;
                State = path == null ? BackendState.Idle : BackendState.Loading;
            }
            if (old != null)
            {
                try { old.Dispose(); }
                catch (Exception e) { Log.Warn("the previous model did not close cleanly: " + e.Message); }
            }
            if (path == null) { Log.Info("model unloaded"); return; }
            Log.Info("loading " + path);
            Thread t = new Thread(delegate() { LoadNew(path); }) { IsBackground = true, Name = "windlass-swap" };
            t.Start();
        }

        void LoadNew(string path)
        {
            Backend b = null;
            try
            {
                b = _factory(path);
                lock (_gate) { _inner = b; ModelPath = path; ModelId = b.ModelId; }
                b.Load();
                Finish(b);
            }
            catch (Exception e)
            {
                string why = e.GetType().Name + ": " + e.Message;
                Log.Error("model failed to load: " + why);
                lock (_gate)
                {
                    _inner = null;
                    ModelPath = path;
                    LoadError = why;
                    State = BackendState.Error;
                }
                if (b != null) { try { b.Dispose(); } catch (Exception) { } }
            }
        }

        public override GenerationResult Generate(GenerationRequest request, Action<string> onText, Action<int, int> onStage, CancellationToken cancel)
        {
            Backend b;
            lock (_gate)
            {
                if (State != BackendState.Ready || _inner == null) throw new BackendException("no model is ready", 503, "model_loading");
                b = _inner;
                _active++;
            }
            try { return b.Generate(request, onText, onStage, cancel); }
            finally { lock (_gate) { _active--; } }
        }

        public override Dictionary<string, object> Facts()
        {
            Backend b;
            lock (_gate) { b = _inner; }
            return b != null ? b.Facts() : new Dictionary<string, object>();
        }

        public override string PlaybookJson()
        {
            Backend b;
            lock (_gate) { b = _inner; }
            return b != null ? b.PlaybookJson() : null;
        }

        public override void Dispose()
        {
            Backend b;
            lock (_gate) { b = _inner; _inner = null; }
            if (b != null) b.Dispose();
        }
    }
}

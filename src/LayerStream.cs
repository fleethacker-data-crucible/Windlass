// Layer streaming: the heart of Windlass. C# 5 only.
// The model is split into stages: one per decoder layer (blk.N.*) and a final "head" stage (output_norm + output).
// A stage's weights are read from disk into one of two RAM slots just before it runs; while it computes, a worker
// reads the next stage into the other slot (prefetch). So RAM holds about two layers, never the whole model.
// Optionally the first stages that fit in --cache-gb stay resident, so they are read only once.
// The technique (one layer resident, prefetch the next) follows AirLLM (Apache-2.0); see THIRD-PARTY.md.
using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace Windlass
{
    // A 2-D weight (Rows output features of Cols inputs) or a 1-D vector (Rows = 1) inside a loaded stage.
    public sealed unsafe class WeightView
    {
        public string Name;
        public GgmlType Type;
        public int Rows, Cols;
        public long RowBytes;
        public byte* Data;
        public int Experts = 1;                  // stacked MoE tensor: Rows is per expert and Data starts at expert 0

        // Expert e of a stacked tensor as an ordinary matrix: row pointer arithmetic only, nothing is read or copied.
        public WeightView Slice(int expert)
        {
            if (expert < 0 || expert >= Experts) throw new ArgumentOutOfRangeException("expert", "Expert " + expert + " is outside 0.." + (Experts - 1) + " of " + Name + ".");
            WeightView v = new WeightView();
            v.Name = Name + "[" + expert + "]";
            v.Type = Type; v.Rows = Rows; v.Cols = Cols; v.RowBytes = RowBytes;
            v.Data = Data + (long)expert * Rows * RowBytes;
            return v;
        }
    }

    public sealed unsafe class Stage
    {
        public int Index;
        public string Label;
        public readonly List<GgufTensor> Tensors = new List<GgufTensor>();
        public long Bytes;                       // including alignment padding
        public readonly Dictionary<string, WeightView> Views = new Dictionary<string, WeightView>();

        public WeightView Get(string name)
        {
            WeightView v;
            if (!Views.TryGetValue(name, out v)) throw new InvalidOperationException(Label + " has no " + name + ".");
            return v;
        }

        public WeightView Find(string name)
        {
            WeightView v;
            return Views.TryGetValue(name, out v) ? v : null;
        }

        // One expert of a stacked MoE tensor (ffn_gate_exps.weight and so on).
        public WeightView Expert(string name, int expert) { return Get(name).Slice(expert); }
    }

    public sealed unsafe class LayerStream : IDisposable
    {
        const int Align = 64;
        const int ChunkBytes = 8 << 20;

        readonly GgufModelFile _g;
        readonly Stage[] _stages;
        readonly IntPtr[] _slot = new IntPtr[2];
        readonly int[] _slotStage = { -1, -1 };
        readonly Task[] _slotTask = new Task[2];
        readonly Dictionary<int, IntPtr> _cached = new Dictionary<int, IntPtr>();
        readonly object _readLock = new object();
        readonly FileStream[] _embedFiles;
        readonly bool _prefetch;
        int _current = -1;                       // slot in use by the running stage, or -1
        long _bytesRead;

        public long SlotBytes { get; private set; }
        public long CachedBytes { get; private set; }
        public int StageCount { get { return _stages.Length; } }
        public int HeadStage { get { return _stages.Length - 1; } }
        public long BytesRead { get { lock (_readLock) return _bytesRead; } }

        public LayerStream(GgufModelFile g, ModelConfig c, bool prefetch, long cacheBytes)
        {
            _g = g;
            _prefetch = prefetch;
            _stages = new Stage[c.Layers + 1];
            for (int i = 0; i < c.Layers; i++)
            {
                _stages[i] = new Stage { Index = i, Label = "layer " + i };
                string prefix = "blk." + i + ".";
                foreach (GgufTensor t in g.Tensors) if (t.Name.StartsWith(prefix, StringComparison.Ordinal)) _stages[i].Tensors.Add(t);
            }
            Stage head = new Stage { Index = c.Layers, Label = "output head" };
            head.Tensors.Add(g.TensorsByName["output_norm.weight"]);
            head.Tensors.Add(c.TiedEmbeddings ? g.TensorsByName["token_embd.weight"] : g.TensorsByName["output.weight"]);
            _stages[c.Layers] = head;

            long max = 0;
            foreach (Stage s in _stages)
            {
                s.Bytes = 0;
                foreach (GgufTensor t in s.Tensors) s.Bytes += (t.SizeBytes + Align - 1) / Align * Align;
                if (s.Bytes > max) max = s.Bytes;
            }
            SlotBytes = max;

            // Resident cache: the first stages that fit the budget are loaded once and kept.
            long budget = cacheBytes;
            for (int i = 0; i < _stages.Length && budget >= _stages[i].Bytes; i++)
            {
                IntPtr p = Marshal.AllocHGlobal(new IntPtr(_stages[i].Bytes));
                _cached[i] = p;
                budget -= _stages[i].Bytes;
                CachedBytes += _stages[i].Bytes;
            }
            if (_cached.Count < _stages.Length)
            {
                _slot[0] = Marshal.AllocHGlobal(new IntPtr(SlotBytes));
                if (prefetch) _slot[1] = Marshal.AllocHGlobal(new IntPtr(SlotBytes));
            }
            foreach (KeyValuePair<int, IntPtr> kv in _cached) Read(_stages[kv.Key], kv.Value);

            _embedFiles = new FileStream[g.Paths.Count];
        }

        // Returns stage i with its weights in RAM. The previously acquired stage must no longer be in use.
        public Stage Acquire(int i)
        {
            Stage s = _stages[i];
            IntPtr cached;
            if (_cached.TryGetValue(i, out cached))
            {
                Bind(s, cached);
                _current = -1;
                StartPrefetch(Next(i));
                return s;
            }
            int slot = -1;
            for (int k = 0; k < 2; k++) if (_slotStage[k] == i && _slotTask[k] != null) slot = k;
            if (slot >= 0) _slotTask[slot].Wait();
            else
            {
                slot = _slot[1] == IntPtr.Zero ? 0 : (_current == 0 ? 1 : 0);
                WaitSlot(slot);
                _slotStage[slot] = i;
                Read(s, _slot[slot]);
                _slotTask[slot] = Task.FromResult(0);
            }
            Bind(s, _slot[slot]);
            _current = slot;
            StartPrefetch(Next(i));
            return s;
        }

        int Next(int i) { return (i + 1) % _stages.Length; }

        void StartPrefetch(int i)
        {
            if (!_prefetch || _cached.ContainsKey(i) || _slot[1] == IntPtr.Zero) return;
            for (int k = 0; k < 2; k++) if (_slotStage[k] == i) return;
            int slot = _current == 0 ? 1 : 0;
            WaitSlot(slot);
            Stage s = _stages[i];
            IntPtr dst = _slot[slot];
            _slotStage[slot] = i;
            _slotTask[slot] = Task.Factory.StartNew(delegate { Read(s, dst); }, TaskCreationOptions.LongRunning);
        }

        void WaitSlot(int slot)
        {
            Task t = _slotTask[slot];
            if (t == null) return;
            try { t.Wait(); }
            catch (AggregateException) { }        // a failed prefetch is retried by the synchronous read
            _slotTask[slot] = null;
            _slotStage[slot] = -1;
        }

        void Bind(Stage s, IntPtr basePtr)
        {
            s.Views.Clear();   // rebuilt on every acquire: the stage may sit in either slot
            long off = 0;
            foreach (GgufTensor t in s.Tensors)
            {
                WeightView v = new WeightView();
                v.Name = t.Name;
                v.Type = t.Type;
                v.Cols = t.Cols;
                v.Experts = t.Experts;
                v.Rows = t.ExpertRows;           // per expert; Views of 2-D weights are unchanged (Experts = 1)
                v.RowBytes = t.SizeBytes / t.Rows;
                v.Data = (byte*)basePtr + off;
                string key = t.Name;
                int dot = key.IndexOf('.');
                if (key.StartsWith("blk.", StringComparison.Ordinal)) key = key.Substring(key.IndexOf('.', dot + 1) + 1);
                if (key == "token_embd.weight") key = "output.weight";
                s.Views[key] = v;
                off += (t.SizeBytes + Align - 1) / Align * Align;
            }
        }

        void Read(Stage s, IntPtr dst)
        {
            byte[] chunk = new byte[ChunkBytes];
            long off = 0;
            FileStream[] files = new FileStream[_g.Paths.Count];
            try
            {
                foreach (GgufTensor t in s.Tensors)
                {
                    FileStream fs = files[t.FileIndex];
                    if (fs == null)
                        fs = files[t.FileIndex] = new FileStream(_g.Paths[t.FileIndex], FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.SequentialScan);
                    fs.Position = t.FileOffset;
                    long left = t.SizeBytes, at = off;
                    while (left > 0)
                    {
                        int want = (int)Math.Min(left, ChunkBytes);
                        int got = fs.Read(chunk, 0, want);
                        if (got <= 0) throw new EndOfStreamException("Unexpected end of " + _g.Paths[t.FileIndex] + " while reading " + t.Name + ".");
                        Marshal.Copy(chunk, 0, new IntPtr((byte*)dst + at), got);
                        at += got; left -= got;
                    }
                    off += (t.SizeBytes + Align - 1) / Align * Align;
                }
            }
            finally { foreach (FileStream fs in files) if (fs != null) fs.Dispose(); }
            lock (_readLock) _bytesRead += s.Bytes;
        }

        // One row of token_embd, read straight from disk (the embedding table is never held in RAM).
        public void EmbedRow(GgufTensor embd, int token, float* dst)
        {
            if (token < 0 || token >= embd.Rows) throw new ArgumentOutOfRangeException("token", "Token id " + token + " is outside the vocabulary.");
            long rowBytes = embd.SizeBytes / embd.Rows;
            byte[] buf = new byte[rowBytes];
            lock (_embedFiles)
            {
                FileStream fs = _embedFiles[embd.FileIndex];
                if (fs == null) fs = _embedFiles[embd.FileIndex] = new FileStream(_g.Paths[embd.FileIndex], FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
                fs.Position = embd.FileOffset + rowBytes * token;
                int at = 0;
                while (at < buf.Length)
                {
                    int got = fs.Read(buf, at, buf.Length - at);
                    if (got <= 0) throw new EndOfStreamException("Unexpected end of file reading token_embd.");
                    at += got;
                }
            }
            fixed (byte* p = buf) Quants.DecodeRow(embd.Type, p, embd.Cols, dst);
        }

        public void Dispose()
        {
            for (int k = 0; k < 2; k++) WaitSlot(k);
            for (int k = 0; k < 2; k++) if (_slot[k] != IntPtr.Zero) { Marshal.FreeHGlobal(_slot[k]); _slot[k] = IntPtr.Zero; }
            foreach (IntPtr p in _cached.Values) Marshal.FreeHGlobal(p);
            _cached.Clear();
            if (_embedFiles != null) foreach (FileStream fs in _embedFiles) if (fs != null) fs.Dispose();
        }
    }
}

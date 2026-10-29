// The machine playbook: Windlass looks at the computer it runs on and picks its own settings. C# 5 only.
// MachineFacts says what the computer has (RAM free now, cores, battery). ModelShape says what the model needs (layer
// sizes, KV cache per token). Playbook.Plan turns the two into settings, with a plain-English reason for each one, so a
// laptop, a server and a desktop each get settings that fit them. `probe` adds measurements (read speed, time per
// layer) so the playbook can also pick a thread count and predict seconds per token. Playbooks are saved per model in
// playbooks\ and reused while the machine and the model file stay the same.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace Windlass
{
    public sealed class MachineFacts
    {
        public string Cpu = "unknown CPU";
        public int LogicalCores, PhysicalCores;
        public long TotalRam, AvailableRam;
        public bool HasBattery, OnBattery;
        public readonly List<string> Gpus = new List<string>();

        // Start-Windlass.ps1 reads the hardware with CIM and passes it in $env:WINDLASS_MACHINE; free RAM is read here,
        // at the moment the settings are chosen.
        public static MachineFacts Detect()
        {
            MachineFacts m = FromJson(Environment.GetEnvironmentVariable("WINDLASS_MACHINE"));
            m.AvailableRam = AvailableBytes();
            if (m.TotalRam <= 0) m.TotalRam = m.AvailableRam;
            return m;
        }

        public static MachineFacts FromJson(string json)
        {
            MachineFacts m = new MachineFacts();
            m.LogicalCores = Environment.ProcessorCount;
            Dictionary<string, object> d = null;
            if (!string.IsNullOrEmpty(json))
            {
                try { d = Json.Parse(json) as Dictionary<string, object>; }
                catch (FormatException) { d = null; }
            }
            if (d != null)
            {
                m.Cpu = Json.GetStr(d, "cpu", m.Cpu).Trim();
                m.TotalRam = (long)Json.GetNum(d, "totalRam", 0);
                m.PhysicalCores = (int)Json.GetNum(d, "physicalCores", 0);
                object v;
                if (d.TryGetValue("hasBattery", out v) && v is bool) m.HasBattery = (bool)v;
                if (d.TryGetValue("onBattery", out v) && v is bool) m.OnBattery = (bool)v;
                if (d.TryGetValue("gpus", out v))
                {
                    if (v is string) m.Gpus.Add((string)v);
                    else if (v is List<object>) foreach (object g in (List<object>)v) if (g is string) m.Gpus.Add((string)g);
                }
            }
            if (m.PhysicalCores <= 0 || m.PhysicalCores > m.LogicalCores) m.PhysicalCores = m.LogicalCores;
            return m;
        }

        static long AvailableBytes()
        {
            try { using (PerformanceCounter pc = new PerformanceCounter("Memory", "Available Bytes", true)) return (long)pc.NextValue(); }
            catch (InvalidOperationException) { return 0; }
            catch (UnauthorizedAccessException) { return 0; }
            catch (System.ComponentModel.Win32Exception) { return 0; }
        }

        // Identifies the hardware; free RAM is left out because it changes all the time.
        public string Key
        {
            get { return Cpu + "|" + LogicalCores + "|" + Math.Round(TotalRam / 1e9).ToString(CultureInfo.InvariantCulture); }
        }
    }

    public sealed class ModelShape
    {
        const int Align = 64;
        public string Id, Key;
        public int Layers, ModelContext;
        public long StreamedBytes;           // every stage (layers + output head)
        public long SlotBytes;               // the largest stage
        public long KvBytesPerToken;         // fp32 K and V for every layer

        public static ModelShape From(GgufModelFile g, ModelConfig c, string id)
        {
            ModelShape s = new ModelShape();
            s.Id = id;
            s.Layers = c.Layers;
            s.ModelContext = c.ContextLength;
            s.KvBytesPerToken = 2L * c.Layers * c.KvDim * 4;
            long[] stage = new long[c.Layers + 1];
            foreach (GgufTensor t in g.Tensors)
            {
                long size = (t.SizeBytes + Align - 1) / Align * Align;
                if (t.Name.StartsWith("blk.", StringComparison.Ordinal))
                {
                    int dot = t.Name.IndexOf('.', 4), layer;
                    if (dot > 4 && int.TryParse(t.Name.Substring(4, dot - 4), NumberStyles.Integer, CultureInfo.InvariantCulture, out layer) && layer < c.Layers)
                        stage[layer] += size;
                }
                else if (t.Name == "output_norm.weight" || t.Name == (c.TiedEmbeddings ? "token_embd.weight" : "output.weight"))
                    stage[c.Layers] += size;
            }
            foreach (long b in stage) { s.StreamedBytes += b; if (b > s.SlotBytes) s.SlotBytes = b; }
            FileInfo f = new FileInfo(g.Paths[0]);
            s.Key = f.Name + "|" + g.TotalBytes + "|" + f.LastWriteTimeUtc.Ticks;
            return s;
        }
    }

    public sealed class Playbook
    {
        const long GB = 1000000000L;
        public const int DefaultContextCap = 8192, MinContext = 2048;

        public string MachineKey, ModelKey, ModelId, Created;
        // Measured by `probe` (0 = not measured).
        public double ReadGBps, LayerSecPhysical, LayerSecLogical;
        // Chosen settings.
        public long CacheBytes;
        public bool Prefetch;
        public int ContextLength, Threads;
        public double PredictedSecPerToken;
        public readonly List<string> Rules = new List<string>();

        public bool Measured { get { return LayerSecPhysical > 0 && LayerSecLogical > 0 && ReadGBps > 0; } }

        static string Gb(long b) { return (b / 1e9).ToString("F1", CultureInfo.InvariantCulture) + " GB"; }

        // The rule book. measured may be null (then the thread count and the prediction use defaults).
        public static Playbook Plan(MachineFacts m, ModelShape s, Playbook measured)
        {
            Playbook p = new Playbook();
            p.MachineKey = m.Key; p.ModelKey = s.Key; p.ModelId = s.Id;
            p.Created = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            if (measured != null) { p.ReadGBps = measured.ReadGBps; p.LayerSecPhysical = measured.LayerSecPhysical; p.LayerSecLogical = measured.LayerSecLogical; }

            // 1. RAM budget: leave room for Windows and the other programs on this computer.
            long reserve = Math.Max(4 * GB, m.TotalRam * 15 / 100);
            long budget = m.AvailableRam - reserve;
            p.Rules.Add("RAM: " + Gb(m.TotalRam) + " total, " + Gb(m.AvailableRam) + " free now. Keeping " + Gb(reserve) +
                        " for Windows and other programs, so Windlass may use " + Gb(Math.Max(budget, 0)) + ".");
            if (budget < s.SlotBytes)
            {
                p.Rules.Add("Very little free RAM: Windlass needs at least one layer (" + Gb(s.SlotBytes) + ") and may slow the computer. Close other programs if you can.");
                budget = s.SlotBytes;
            }

            // 2. Context: the KV cache grows with the context and must fit beside the layers.
            int ctx = Math.Min(s.ModelContext > 0 ? s.ModelContext : DefaultContextCap, DefaultContextCap);
            int wanted = ctx;
            while (ctx > MinContext && s.KvBytesPerToken * (long)ctx > budget / 4) ctx /= 2;
            p.ContextLength = ctx;
            long kv = s.KvBytesPerToken * (long)ctx;
            p.Rules.Add(ctx < wanted
                ? "Context " + ctx + " tokens (not " + wanted + "): its KV cache (" + Gb(kv) + ") is kept to a quarter of the budget."
                : "Context " + ctx + " tokens; its KV cache needs up to " + Gb(kv) + ".");

            // 3. Prefetch: a second layer slot lets the next layer load while this one computes.
            long left = budget - kv;
            p.Prefetch = left >= 2 * s.SlotBytes;
            p.Rules.Add(p.Prefetch
                ? "Prefetch on: two layer slots of " + Gb(s.SlotBytes) + ", so reading the next layer overlaps computing this one."
                : "Prefetch off: not enough RAM for a second layer slot, so reading and computing take turns.");
            left -= s.SlotBytes * (p.Prefetch ? 2 : 1);

            // 4. Pin layers: whatever RAM is left keeps layers resident, so they are read once instead of every token.
            p.CacheBytes = Math.Max(0, Math.Min(left, s.StreamedBytes));
            if (p.CacheBytes >= s.StreamedBytes)
                p.Rules.Add("The whole model (" + Gb(s.StreamedBytes) + ") fits: every layer stays in RAM after loading, so a token reads nothing from disk.");
            else if (p.CacheBytes >= s.SlotBytes)
                p.Rules.Add("Pinning " + Gb(p.CacheBytes) + " of " + Gb(s.StreamedBytes) + " (" + (100 * p.CacheBytes / s.StreamedBytes) +
                            "%) in RAM; the rest streams from disk for every token.");
            else
            {
                p.CacheBytes = 0;
                p.Rules.Add("No RAM to spare for pinned layers: every layer streams from disk for every token.");
            }

            // 5. Threads: on battery use one thread per physical core (cooler, longer battery); otherwise the faster
            //    of physical and logical as measured, or every logical core when not measured.
            if (m.OnBattery)
            {
                p.Threads = m.PhysicalCores;
                p.Rules.Add("On battery: " + p.Threads + " threads (one per physical core) to save power and heat. Plug in for full speed.");
            }
            else if (p.LayerSecPhysical > 0 && p.LayerSecLogical > 0 && m.PhysicalCores < m.LogicalCores)
            {
                bool phys = p.LayerSecPhysical <= p.LayerSecLogical * 0.97;
                p.Threads = phys ? m.PhysicalCores : m.LogicalCores;
                double mine = phys ? p.LayerSecPhysical : p.LayerSecLogical, other = phys ? p.LayerSecLogical : p.LayerSecPhysical;
                p.Rules.Add(p.Threads + " threads: measured " + (mine < other * 0.97 ? "faster" : "no slower") + " than " + (phys ? m.LogicalCores : m.PhysicalCores) +
                            " (" + mine.ToString("F3", CultureInfo.InvariantCulture) + " s vs " + other.ToString("F3", CultureInfo.InvariantCulture) + " s per layer).");
            }
            else
            {
                p.Threads = m.LogicalCores;
                p.Rules.Add(p.Threads + " threads (every logical core). Run `probe` to measure whether fewer are faster here.");
            }

            // 6. Prediction from the measurements: reading the unpinned share vs computing every layer.
            if (p.LayerSecPhysical > 0 && p.ReadGBps > 0)
            {
                double compute = s.Layers * (p.Threads == m.PhysicalCores && m.PhysicalCores < m.LogicalCores ? p.LayerSecPhysical : p.LayerSecLogical);
                double read = (s.StreamedBytes - p.CacheBytes) / (p.ReadGBps * GB);
                p.PredictedSecPerToken = p.Prefetch ? Math.Max(compute, read) : compute + read;
                p.Rules.Add("Expect about " + p.PredictedSecPerToken.ToString("F1", CultureInfo.InvariantCulture) + " s per token (compute " +
                            compute.ToString("F1", CultureInfo.InvariantCulture) + " s, reading " + read.ToString("F1", CultureInfo.InvariantCulture) + " s" +
                            (read > compute ? "; the disk is the limit" : "; the CPU is the limit") + ").");
            }
            return p;
        }

        // ---------- saving ----------

        public static string PathFor(string root, string modelId)
        {
            string safe = modelId;
            foreach (char c in Path.GetInvalidFileNameChars()) safe = safe.Replace(c, '_');
            return Path.Combine(Path.Combine(root, "playbooks"), safe + ".json");
        }

        public string ToJson(MachineFacts m)
        {
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["schema"] = "windlass.playbook/1"; d["created"] = Created; d["model"] = ModelId; d["modelKey"] = ModelKey; d["machineKey"] = MachineKey;
            Dictionary<string, object> mf = new Dictionary<string, object>();
            mf["cpu"] = m.Cpu; mf["logicalCores"] = (long)m.LogicalCores; mf["physicalCores"] = (long)m.PhysicalCores;
            mf["totalRamGB"] = Math.Round(m.TotalRam / 1e9, 1); mf["freeRamGB"] = Math.Round(m.AvailableRam / 1e9, 1);
            mf["onBattery"] = m.OnBattery; mf["hasBattery"] = m.HasBattery;
            List<object> gpus = new List<object>(); foreach (string g in m.Gpus) gpus.Add(g); mf["gpus"] = gpus;
            d["machine"] = mf;
            Dictionary<string, object> me = new Dictionary<string, object>();
            me["readGBps"] = ReadGBps; me["layerSecPhysical"] = LayerSecPhysical; me["layerSecLogical"] = LayerSecLogical;
            d["measured"] = me;
            Dictionary<string, object> st = new Dictionary<string, object>();
            st["cacheGB"] = Math.Round(CacheBytes / 1e9, 2); st["prefetch"] = Prefetch; st["contextLength"] = (long)ContextLength;
            st["threads"] = (long)Threads; st["predictedSecPerToken"] = PredictedSecPerToken;
            d["settings"] = st;
            List<object> rules = new List<object>(); foreach (string r in Rules) rules.Add(r); d["rules"] = rules;
            return Json.Write(d);
        }

        // The measurements of a saved playbook, if it was made on this machine for this model file; otherwise null.
        public static Playbook LoadMeasured(string path, MachineFacts m, ModelShape s)
        {
            if (!File.Exists(path)) return null;
            Dictionary<string, object> d;
            try { d = Json.Parse(File.ReadAllText(path)) as Dictionary<string, object>; }
            catch (FormatException) { return null; }
            catch (IOException) { return null; }
            if (d == null || Json.GetStr(d, "machineKey", "") != m.Key || Json.GetStr(d, "modelKey", "") != s.Key) return null;
            object me;
            if (!d.TryGetValue("measured", out me) || !(me is Dictionary<string, object>)) return null;
            Dictionary<string, object> mm = (Dictionary<string, object>)me;
            Playbook p = new Playbook();
            p.ReadGBps = Json.GetNum(mm, "readGBps", 0);
            p.LayerSecPhysical = Json.GetNum(mm, "layerSecPhysical", 0);
            p.LayerSecLogical = Json.GetNum(mm, "layerSecLogical", 0);
            return p.Measured ? p : null;
        }

        public void Save(string path, MachineFacts m)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, ToJson(m));
        }
    }
}

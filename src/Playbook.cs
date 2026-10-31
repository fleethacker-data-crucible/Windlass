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
        public long KvBytesPerToken;         // fp16 K and V for every layer

        public static ModelShape From(GgufModelFile g, ModelConfig c, string id)
        {
            ModelShape s = new ModelShape();
            s.Id = id;
            s.Layers = c.Layers;
            s.ModelContext = c.ContextLength;
            s.KvBytesPerToken = 2L * c.Layers * c.KvDim * 2;    // fp16 K and V
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
        // Measured by `probe` (0 = not measured). LayerSecBatch: one layer for a token plus MaxGuesses guesses at once.
        public double ReadGBps, LayerSecPhysical, LayerSecLogical, LayerSecBatch;
        // Measured by `playbook eval`: perplexity on the built-in passage (lower is better) and prompt reading speed.
        public double QualityPerplexity, PromptSecPerToken;
        public int QualityTokens;
        public string QualityMeasured;       // when playbook eval ran (UTC, ISO 8601); null = never
        // Measured by `playbook fidelity`: planted facts recalled correctly out of those asked, in a log of
        // FidelityPromptTokens tokens (FidelityTotal 0 = not measured).
        public int FidelityCorrect, FidelityTotal, FidelityPromptTokens, FidelityVersion;
        public string FidelityMeasured;
        // Observed in real runs on this machine (totals since the playbook was measured).
        public long ObsRuns, ObsPromptTokens, ObsTokens, ObsGuessed, ObsAccepted;
        public double ObsPromptSeconds, ObsSeconds;
        // Chosen settings.
        public long CacheBytes;
        public bool Prefetch, Speculate;
        public int ContextLength, Threads;
        public double PredictedSecPerToken;
        public string Limit;                 // "disk" or "cpu" when a probe let the playbook name it, else null
        public readonly List<string> Rules = new List<string>();
        readonly object _obs = new object();  // real runs update the totals while /v1/models reads them

        public bool Measured { get { return LayerSecPhysical > 0 && LayerSecLogical > 0 && ReadGBps > 0; } }
        public const int MinGuessesToJudge = 200;
        public const double MinKeptRate = 0.15, MaxBatchCost = 2.5;
        // Real-run totals become a published "observed" speed only once they cover this many tokens.
        public const int MinObservedTokens = 20;

        public void SetQuality(QualityResult q)
        {
            QualityPerplexity = q.Perplexity; QualityTokens = q.Tokens; PromptSecPerToken = q.SecPerPromptToken;
            QualityMeasured = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        public void SetFidelity(FidelityResult f)
        {
            FidelityCorrect = f.Correct; FidelityTotal = f.Total; FidelityPromptTokens = f.PromptTokens; FidelityVersion = Fidelity.Version;
            FidelityMeasured = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
        }

        // Keeps what `playbook eval` and `playbook fidelity` measured when the speed side is measured again.
        public void CopyScoresFrom(Playbook h)
        {
            QualityPerplexity = h.QualityPerplexity; QualityTokens = h.QualityTokens; PromptSecPerToken = h.PromptSecPerToken; QualityMeasured = h.QualityMeasured;
            FidelityCorrect = h.FidelityCorrect; FidelityTotal = h.FidelityTotal; FidelityPromptTokens = h.FidelityPromptTokens;
            FidelityVersion = h.FidelityVersion; FidelityMeasured = h.FidelityMeasured;
        }

        public void Observe(int promptTokens, double promptSeconds, int tokens, double seconds, int guessed, int accepted)
        {
            lock (_obs)
            {
                ObsRuns++;
                ObsPromptTokens += promptTokens; ObsPromptSeconds += promptSeconds;
                ObsTokens += tokens; ObsSeconds += seconds;
                ObsGuessed += guessed; ObsAccepted += accepted;
            }
        }

        // One published speed figure. Values never measured are null, never a guess.
        static Dictionary<string, object> Figure(double predicted, string predictedSource, long obsCount, double obsSeconds)
        {
            object pred = predicted > 0 ? (object)predicted : null;
            object obs = obsCount >= MinObservedTokens && obsSeconds > 0 ? (object)(obsSeconds / obsCount) : null;
            Dictionary<string, object> d = new Dictionary<string, object>();
            d["value"] = obs ?? pred;
            d["predicted"] = pred;
            d["observed"] = obs;
            d["measured"] = (obs ?? pred) != null;
            d["source"] = obs != null ? "real-runs" : pred != null ? predictedSource : "none";
            d["samples"] = obsCount;
            return d;
        }

        // What this playbook measured, for GET /v1/models (the `windlass` object; see docs/API.md). Only the keys that
        // are known appear; ModelFacts fills the rest with null. Safe to call while a request is running.
        public Dictionary<string, object> ToFacts()
        {
            Dictionary<string, object> f = new Dictionary<string, object>();
            lock (_obs)
            {
                Dictionary<string, object> speed = new Dictionary<string, object>();
                speed["secPerToken"] = Figure(PredictedSecPerToken, "probe", ObsTokens, ObsSeconds);
                speed["secPerPromptToken"] = Figure(PromptSecPerToken, "eval", ObsPromptTokens, ObsPromptSeconds);
                speed["limit"] = Limit;
                f["speed"] = speed;
                Dictionary<string, object> q = new Dictionary<string, object>();
                if (QualityPerplexity > 0)
                {
                    q["perplexity"] = QualityPerplexity; q["tokens"] = (long)QualityTokens; q["measuredAt"] = QualityMeasured;
                }
                f["quality"] = q;
                Dictionary<string, object> fi = new Dictionary<string, object>();
                if (FidelityTotal > 0)
                {
                    fi["score"] = (double)FidelityCorrect / FidelityTotal; fi["correct"] = (long)FidelityCorrect; fi["total"] = (long)FidelityTotal;
                    fi["promptTokens"] = (long)FidelityPromptTokens; fi["version"] = (long)FidelityVersion; fi["measuredAt"] = FidelityMeasured;
                }
                f["fidelity"] = fi;
            }
            return f;
        }

        static string Gb(long b) { return (b / 1e9).ToString("F1", CultureInfo.InvariantCulture) + " GB"; }

        static string F(double v, string fmt) { return v.ToString(fmt, CultureInfo.InvariantCulture); }

        // The rule book. history (measurements and real runs from earlier) may be null: then the thread count, the
        // guess-ahead choice and the prediction fall back to defaults.
        public static Playbook Plan(MachineFacts m, ModelShape s, Playbook history)
        {
            Playbook p = new Playbook();
            p.MachineKey = m.Key; p.ModelKey = s.Key; p.ModelId = s.Id;
            p.Created = DateTime.UtcNow.ToString("o", CultureInfo.InvariantCulture);
            if (history != null)
            {
                p.ReadGBps = history.ReadGBps; p.LayerSecPhysical = history.LayerSecPhysical;
                p.LayerSecLogical = history.LayerSecLogical; p.LayerSecBatch = history.LayerSecBatch;
                p.ObsRuns = history.ObsRuns; p.ObsPromptTokens = history.ObsPromptTokens; p.ObsPromptSeconds = history.ObsPromptSeconds;
                p.ObsTokens = history.ObsTokens; p.ObsSeconds = history.ObsSeconds;
                p.ObsGuessed = history.ObsGuessed; p.ObsAccepted = history.ObsAccepted;
                p.CopyScoresFrom(history);
            }

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
            double read = 0;
            if (p.LayerSecPhysical > 0 && p.ReadGBps > 0)
            {
                double compute = s.Layers * (p.Threads == m.PhysicalCores && m.PhysicalCores < m.LogicalCores ? p.LayerSecPhysical : p.LayerSecLogical);
                read = (s.StreamedBytes - p.CacheBytes) / (p.ReadGBps * GB);
                p.PredictedSecPerToken = p.Prefetch ? Math.Max(compute, read) : compute + read;
                p.Limit = read > compute ? "disk" : "cpu";
                p.Rules.Add("Expect about " + F(p.PredictedSecPerToken, "F1") + " s per token (compute " + F(compute, "F1") + " s, reading " + F(read, "F1") + " s" +
                            (read > compute ? "; the disk is the limit" : "; the CPU is the limit") + ").");
            }

            // 7. Guess-ahead (greedy replies only): checking a token plus several guesses costs one pass. It pays off
            //    when that pass is cheap next to a normal token (reading dominates) and enough guesses turn out right.
            double kept = p.ObsGuessed > 0 ? (double)p.ObsAccepted / p.ObsGuessed : 0;
            if (p.ObsGuessed >= MinGuessesToJudge && kept < MinKeptRate)
            {
                p.Speculate = false;
                p.Rules.Add("Guess-ahead off: in real runs here only " + F(100 * kept, "F0") + "% of " + p.ObsGuessed + " guesses were right, too few to pay for checking them.");
            }
            else if (p.LayerSecBatch > 0 && p.PredictedSecPerToken > 0)
            {
                double batchCompute = s.Layers * p.LayerSecBatch;
                double batch = p.Prefetch ? Math.Max(batchCompute, read) : batchCompute + read;
                double cost = batch / p.PredictedSecPerToken;
                p.Speculate = cost <= MaxBatchCost;
                p.Rules.Add(p.Speculate
                    ? "Guess-ahead on for greedy replies: checking a token and " + LayeredBackend.MaxGuesses + " guesses costs " + F(cost, "F1") + "x a normal token, so it pays off once about " + Math.Max(1, (int)Math.Ceiling(cost - 1)) + " guesses per pass are right."
                    : "Guess-ahead off: checking " + LayeredBackend.MaxGuesses + " guesses costs " + F(cost, "F1") + "x a normal token on this machine, more than the guesses usually save.");
            }
            else
            {
                p.Speculate = p.CacheBytes < s.StreamedBytes;
                p.Rules.Add(p.Speculate
                    ? "Guess-ahead on for greedy replies: most of a token's time goes to reading layers, and a batch of guesses shares that read."
                    : "Guess-ahead off: the whole model is in RAM, so guesses cost extra math. Run `playbook probe` to measure whether they pay off here.");
            }

            // 8. Quality, when `playbook eval` has scored this model here.
            if (p.QualityPerplexity > 0)
                p.Rules.Add("Quality: perplexity " + F(p.QualityPerplexity, "F2") + " on " + p.QualityTokens + " tokens of the built-in passage (lower is better; compare only models with the same tokenizer). " +
                            "Prompts read at " + F(p.PromptSecPerToken, "F2") + " s per token, so a 1,000-token prompt takes about " + F(1000 * p.PromptSecPerToken / 60, "F0") + " min.");

            // 8b. Context fidelity, when `playbook fidelity` has checked this model here.
            if (p.FidelityTotal > 0)
                p.Rules.Add("Context fidelity: " + p.FidelityCorrect + " of " + p.FidelityTotal + " facts planted in a " + p.FidelityPromptTokens +
                            "-token log were recalled (" + F(100.0 * p.FidelityCorrect / p.FidelityTotal, "F0") + "%). A short log says little about long conversations; run `playbook fidelity --tokens <n>` for a longer one.");

            // 9. What real runs on this machine showed.
            if (p.ObsTokens >= 20 && p.ObsSeconds > 0)
            {
                string line = "Recent runs: " + F(p.ObsSeconds / p.ObsTokens, "F2") + " s per reply token over " + p.ObsTokens + " tokens in " + p.ObsRuns + " replies";
                if (p.ObsPromptTokens > 0 && p.ObsPromptSeconds > 0) line += ", " + F(p.ObsPromptSeconds / p.ObsPromptTokens, "F2") + " s per prompt token";
                if (p.ObsGuessed > 0) line += ", " + F(100 * kept, "F0") + "% of guesses kept";
                if (p.PredictedSecPerToken > 0) line += " (predicted " + F(p.PredictedSecPerToken, "F2") + " s per token)";
                p.Rules.Add(line + ".");
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
            me["readGBps"] = ReadGBps; me["layerSecPhysical"] = LayerSecPhysical; me["layerSecLogical"] = LayerSecLogical; me["layerSecBatch"] = LayerSecBatch;
            d["measured"] = me;
            Dictionary<string, object> ob = new Dictionary<string, object>();
            ob["runs"] = ObsRuns; ob["promptTokens"] = ObsPromptTokens; ob["promptSeconds"] = ObsPromptSeconds;
            ob["tokens"] = ObsTokens; ob["seconds"] = ObsSeconds; ob["guessed"] = ObsGuessed; ob["accepted"] = ObsAccepted;
            d["observed"] = ob;
            Dictionary<string, object> q = new Dictionary<string, object>();
            q["perplexity"] = QualityPerplexity; q["tokens"] = (long)QualityTokens; q["promptSecPerToken"] = PromptSecPerToken;
            q["passage"] = "Windlass test passage v1 (original, built in)";
            q["measured"] = QualityMeasured;
            d["quality"] = q;
            Dictionary<string, object> fi = new Dictionary<string, object>();
            fi["correct"] = (long)FidelityCorrect; fi["total"] = (long)FidelityTotal; fi["promptTokens"] = (long)FidelityPromptTokens;
            fi["score"] = FidelityTotal > 0 ? (object)((double)FidelityCorrect / FidelityTotal) : null;
            fi["version"] = (long)FidelityVersion; fi["measured"] = FidelityMeasured;
            d["fidelity"] = fi;
            Dictionary<string, object> st = new Dictionary<string, object>();
            st["cacheGB"] = Math.Round(CacheBytes / 1e9, 2); st["prefetch"] = Prefetch; st["contextLength"] = (long)ContextLength;
            st["threads"] = (long)Threads; st["speculate"] = Speculate; st["predictedSecPerToken"] = PredictedSecPerToken; st["limit"] = Limit;
            d["settings"] = st;
            List<object> rules = new List<object>(); foreach (string r in Rules) rules.Add(r); d["rules"] = rules;
            return Json.Write(d);
        }

        // The measurements and real-run totals of a saved playbook, if it was made on this machine for this model file;
        // otherwise null (a new computer or a changed model starts over).
        public static Playbook LoadHistory(string path, MachineFacts m, ModelShape s)
        {
            if (!File.Exists(path)) return null;
            Dictionary<string, object> d;
            try { d = Json.Parse(File.ReadAllText(path)) as Dictionary<string, object>; }
            catch (FormatException) { return null; }
            catch (IOException) { return null; }
            if (d == null || Json.GetStr(d, "machineKey", "") != m.Key || Json.GetStr(d, "modelKey", "") != s.Key) return null;
            Playbook p = new Playbook();
            object v;
            if (d.TryGetValue("measured", out v) && v is Dictionary<string, object>)
            {
                Dictionary<string, object> mm = (Dictionary<string, object>)v;
                p.ReadGBps = Json.GetNum(mm, "readGBps", 0);
                p.LayerSecPhysical = Json.GetNum(mm, "layerSecPhysical", 0);
                p.LayerSecLogical = Json.GetNum(mm, "layerSecLogical", 0);
                p.LayerSecBatch = Json.GetNum(mm, "layerSecBatch", 0);
            }
            if (d.TryGetValue("observed", out v) && v is Dictionary<string, object>)
            {
                Dictionary<string, object> ob = (Dictionary<string, object>)v;
                p.ObsRuns = (long)Json.GetNum(ob, "runs", 0);
                p.ObsPromptTokens = (long)Json.GetNum(ob, "promptTokens", 0); p.ObsPromptSeconds = Json.GetNum(ob, "promptSeconds", 0);
                p.ObsTokens = (long)Json.GetNum(ob, "tokens", 0); p.ObsSeconds = Json.GetNum(ob, "seconds", 0);
                p.ObsGuessed = (long)Json.GetNum(ob, "guessed", 0); p.ObsAccepted = (long)Json.GetNum(ob, "accepted", 0);
            }
            if (d.TryGetValue("quality", out v) && v is Dictionary<string, object>)
            {
                Dictionary<string, object> q = (Dictionary<string, object>)v;
                p.QualityPerplexity = Json.GetNum(q, "perplexity", 0);
                p.QualityTokens = (int)Json.GetNum(q, "tokens", 0);
                p.PromptSecPerToken = Json.GetNum(q, "promptSecPerToken", 0);
                p.QualityMeasured = Json.GetStr(q, "measured", null);
            }
            if (d.TryGetValue("fidelity", out v) && v is Dictionary<string, object>)
            {
                Dictionary<string, object> fi = (Dictionary<string, object>)v;
                p.FidelityCorrect = (int)Json.GetNum(fi, "correct", 0); p.FidelityTotal = (int)Json.GetNum(fi, "total", 0);
                p.FidelityPromptTokens = (int)Json.GetNum(fi, "promptTokens", 0); p.FidelityVersion = (int)Json.GetNum(fi, "version", 0);
                p.FidelityMeasured = Json.GetStr(fi, "measured", null);
            }
            return p;
        }

        public void Save(string path, MachineFacts m)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            File.WriteAllText(path, ToJson(m), new System.Text.UTF8Encoding(false));
        }
    }
}

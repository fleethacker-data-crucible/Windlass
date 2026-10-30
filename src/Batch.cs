// Batching support (--batch N): the per-sequence KV cache and the RAM check. C# 5 only.
// The batch loop itself is in BatchEngine.cs; Transformer.ForwardBatch does the stacked pass.
using System;
using System.Collections.Generic;
using System.Globalization;

namespace Windlass
{
    // One sequence's KV cache (a "slot"): per layer [position][KvDim] of fp16 keys and values, like llama.cpp's default.
    // Grows on demand up to the context (Transformer.Reserve), so an idle slot costs nothing until it is used.
    // Tokens are the ids whose K/V the slot holds, in order: the prefix a later request may reuse.
    public sealed class Sequence
    {
        public readonly ushort[][] K, V;
        public int Capacity;                     // positions allocated
        public int Length;                       // positions held (valid)
        public readonly List<int> Tokens = new List<int>();
        public bool Busy;                        // a request is generating on it

        public Sequence(int layers) { K = new ushort[layers][]; V = new ushort[layers][]; }

        // Drops cached positions from n onward.
        public void Truncate(int n) { if (n < Length) Length = Math.Max(0, n); }

        // Bytes of RAM allocated now.
        public long Bytes
        {
            get
            {
                long b = 0;
                for (int l = 0; l < K.Length; l++) if (K[l] != null) b += (K[l].LongLength + V[l].LongLength) * 2;
                return b;
            }
        }
    }

    public static class BatchPlan
    {
        public const int MaxBatch = 64;

        // KV cache bytes for `batch` slots at `context` positions (fp16 K and V for every layer; the same figure
        // ModelShape.KvBytesPerToken and `info` use, times the slots).
        public static long KvBytes(ModelConfig c, int context, int batch)
        {
            return 2L * c.Layers * c.KvDim * 2 * (long)context * batch;
        }

        // The RAM the playbook lets Windlass use: free RAM minus a reserve for Windows and other programs (15% of
        // total, at least 4 GB). The same rule as Playbook.Plan.
        public static long Budget(MachineFacts m)
        {
            long reserve = Math.Max(4000000000L, m.TotalRam * 15 / 100);
            return m.AvailableRam - reserve;
        }

        // The playbook keeps one sequence's KV cache within a quarter of the budget. With --batch the slots share
        // that quarter. Returns null when it fits, else why not. m may be null (nothing to check against).
        public static string Check(ModelConfig c, int context, int batch, MachineFacts m)
        {
            if (batch <= 1 || m == null || m.AvailableRam <= 0) return null;
            long need = KvBytes(c, context, batch), room = Math.Max(0, Budget(m)) / 4;
            if (need <= room) return null;
            return "--batch " + batch + " does not fit: " + batch + " KV caches of " + Gb(KvBytes(c, context, 1)) + " (context " + context +
                   ") need up to " + Gb(need) + ", and this computer allows " + Gb(room) + " for them (a quarter of the " + Gb(Math.Max(0, Budget(m))) +
                   " of RAM Windlass may use, with " + Gb(m.AvailableRam) + " free now). Use a smaller --batch or --context-length.";
        }

        // The largest context (a power-of-two step down from `context`, not below minContext) at which Check passes,
        // or `context` unchanged when nothing smaller fits either.
        public static int FitContext(ModelConfig c, int context, int batch, MachineFacts m, int minContext)
        {
            int ctx = context;
            while (ctx > minContext && Check(c, ctx, batch, m) != null) ctx /= 2;
            return Check(c, ctx, batch, m) == null ? ctx : context;
        }

        static string Gb(long b) { return (b / 1e9).ToString("F2", CultureInfo.InvariantCulture) + " GB"; }
    }
}

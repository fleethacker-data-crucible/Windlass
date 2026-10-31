// Features that landed in separate branches meet here: batching with mixture-of-experts, Granite and OLMo 2 models, and
// with the integer dot products. A batched step must give each sequence exactly the logits a single-token step gives it.
// C# 5 only; tiny models.
using System;
using System.IO;
using System.Threading;

namespace Windlass.Tests
{
    public static partial class Runner
    {
        static void BatchMeetsNewModelsAndIntDot()
        {
            string dir = Path.Combine(Path.GetTempPath(), "windlass-integ-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            bool was = Transformer.IntDot;
            try
            {
                string[] paths =
                {
                    TinyMoe.Write(Path.Combine(dir, "a.gguf"), new MoeSpec { Layout = "qwen3moe", Type = GgmlType.Q4_K }),
                    TinyMoe.Write(Path.Combine(dir, "b.gguf"), new MoeSpec { Layout = "qwen2moe", Type = GgmlType.Q6_K, Tied = true }),
                    TinyMoe.Write(Path.Combine(dir, "c.gguf"), new MoeSpec { Layout = "mixtral", Type = GgmlType.Q8_0 }),
                    TinyArchs.Write(Path.Combine(dir, "d.gguf"), new ArchSpec { Arch = "granite", Type = GgmlType.Q8_0, EmbeddingScale = 2f, ResidualScale = 0.5f, AttentionScale = 0.1f, LogitScale = 4f }),
                    TinyArchs.Write(Path.Combine(dir, "e.gguf"), new ArchSpec { Arch = "olmo2", Type = GgmlType.Q4_K })
                };
                foreach (bool intDot in new bool[] { false, true })
                    foreach (string p in paths)
                    {
                        Transformer.IntDot = intDot;
                        GgufModelFile g = GgufModelFile.Open(p);
                        ModelConfig c = ModelConfig.From(g);
                        int[][] prompts = { new int[] { 1, 10, 50 }, new int[] { 7, 99, 33, 72, 5 } };
                        int[] next = { 100, 200 };
                        float[][] want = new float[2][];
                        for (int i = 0; i < 2; i++)
                            using (LayerStream st = new LayerStream(g, c, true, 0))
                            {
                                Transformer tr = new Transformer(g, c, st, 256);
                                Sequence sq = tr.NewSequence();
                                tr.Forward(sq, prompts[i], 0, false, CancellationToken.None, null);
                                want[i] = tr.Forward(sq, new int[] { next[i] }, prompts[i].Length, false, CancellationToken.None, null);
                            }
                        using (LayerStream st = new LayerStream(g, c, true, 0))
                        {
                            Transformer tr = new Transformer(g, c, st, 256);
                            Sequence[] seqs = { tr.NewSequence(), tr.NewSequence() };
                            for (int i = 0; i < 2; i++) tr.Forward(seqs[i], prompts[i], 0, false, CancellationToken.None, null);
                            float[] got = tr.ForwardBatch(seqs, next, CancellationToken.None, null);
                            int V = want[0].Length;
                            Check(got.Length == 2 * V, Path.GetFileName(p) + ": batch logits size");
                            for (int i = 0; i < 2; i++)
                                for (int v = 0; v < V; v++)
                                    if (got[i * V + v] != want[i][v]) throw new TestFailure(Path.GetFileName(p) + (intDot ? " (int dot)" : "") + ": sequence " + i + " logit " + v + " differs in a batch (" + got[i * V + v] + " vs " + want[i][v] + ")");
                        }
                    }
            }
            finally
            {
                Transformer.IntDot = was;
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }
    }
}

// Tests for the faster CPU math (src\FastMath.cs): the register-blocked float kernel and the integer dot products.
// C# 5 only. Tiny shapes and tiny models; no timing here (see docs\ARCHITECTURE.md for what is and is not measured).
using System;
using System.IO;
using System.Threading;

namespace Windlass.Tests
{
    public static unsafe class FastMathTests
    {
        static void Check(bool ok, string what) { if (!ok) throw new Runner.TestFailure(what); }

        // Blocked kernel: right on shapes that exercise every tail (rows not a multiple of 4, odd token counts, odd
        // widths), and a token's result is bit-identical whether it runs alone or with other tokens.
        public static void BlockedKernel()
        {
            foreach (int rows in new int[] { 1, 3, 4, 7, 9 })
                foreach (int T in new int[] { 1, 2, 3, 5 })
                {
                    const int cols = 37;
                    Rng rng = new Rng((ulong)(rows * 31 + T));
                    float[] w = new float[rows * cols], x = new float[T * cols], y = new float[T * rows];
                    for (int i = 0; i < w.Length; i++) w[i] = rng.Normal();
                    for (int i = 0; i < x.Length; i++) x[i] = rng.Normal();
                    fixed (float* wp = w) fixed (float* xp = x) fixed (float* yp = y)
                    {
                        FastMath.Block(wp, cols, rows, xp, cols, T, cols, yp, rows);
                        for (int t = 0; t < T; t++)
                            for (int r = 0; r < rows; r++)
                            {
                                double want = 0, mag = 0;
                                for (int i = 0; i < cols; i++) { want += (double)w[r * cols + i] * x[t * cols + i]; mag += Math.Abs((double)w[r * cols + i] * x[t * cols + i]); }
                                float got = y[t * rows + r];
                                Check(Math.Abs(got - want) <= 1e-5 * mag + 1e-6, "rows " + rows + " T " + T + " (r " + r + ", t " + t + "): " + got + " vs " + want);
                                float[] alone = new float[rows];
                                fixed (float* ap = alone) FastMath.Block(wp, cols, rows, xp + t * cols, cols, 1, cols, ap, rows);
                                Check(alone[r] == got, "token " + t + " row " + r + " changes with the batch: " + alone[r] + " vs " + got);
                            }
                    }
                }
        }

        // Activation quantizers follow ggml's reference functions (hand-checked values).
        public static void ActivationQuantizers()
        {
            float[] x = new float[32];
            for (int i = 0; i < 32; i++) x[i] = (i - 16) * 0.5f;      // -8 .. 7.5, amax 8
            FastMath.QActs a = FastMath.Quantize(x, 1, 32, false);
            float d = 8f / 127f;
            Check(a.D[0] == Half.Round(d), "Q8_0 scale is amax/127 stored as half: " + a.D[0]);
            Check(a.Q[0] == -127 && a.Q[16] == 0, "Q8_0 extremes: " + a.Q[0] + ", " + a.Q[16]);
            Check(a.Q[17] == (sbyte)Math.Round(0.5f / d, MidpointRounding.AwayFromZero), "Q8_0 rounds half away from zero");

            float[] y = new float[256];
            for (int i = 0; i < 256; i++) y[i] = (i % 7) - 3f;          // -3 .. 3
            y[10] = 6f;                                                 // the largest value, positive
            FastMath.QActs k = FastMath.Quantize(y, 1, 256, true);
            Check(k.Q[10] == -127, "Q8_K maps the largest-magnitude value to -127 (ggml's sign convention): " + k.Q[10]);
            Check(Math.Abs(k.D[0] * k.Q[10] - 6f) < 1e-5f, "dequantized largest value is 6: " + k.D[0] * k.Q[10]);
            for (int j = 0; j < 16; j++)
            {
                int s = 0;
                for (int l = 0; l < 16; l++) s += k.Q[j * 16 + l];
                Check(k.Bsums[j] == s, "Q8_K bsums[" + j + "]");
            }
            float[] zeros = new float[256];
            FastMath.QActs z = FastMath.Quantize(zeros, 1, 256, true);
            Check(z.D[0] == 0 && z.Q[5] == 0, "an all-zero block quantizes to zeros");
        }

        // Each integer kernel equals the straightforward computation: decode the weights, dequantize the activations
        // the way the quantizer rounded them, and multiply in double. Also within activation-rounding error of the
        // float kernel.
        public static void IntegerDotKernels()
        {
            foreach (GgmlType t in new GgmlType[] { GgmlType.Q8_0, GgmlType.Q4_0, GgmlType.Q4_K, GgmlType.Q5_K, GgmlType.Q6_K })
            {
                Check(FastMath.CanIntDot(t), GgmlTypes.Name(t) + " has an integer kernel");
                const int cols = 512, rows = 4, T = 3;
                byte[] data = TinyModels.Matrix(new Rng(5), t, rows, cols, 0.5f);
                float[] x = new float[T * cols], row = new float[cols];
                Rng rng = new Rng(11);
                for (int i = 0; i < x.Length; i++) x[i] = rng.Normal();
                FastMath.QActs a = FastMath.Quantize(x, T, cols, FastMath.NeedsK(t));
                int bsz = a.K ? 256 : 32, nb = cols / bsz;
                fixed (byte* p = data) fixed (float* xp = x) fixed (float* rp = row)
                fixed (sbyte* qp = a.Q) fixed (float* dp = a.D)
                {
                    short[] bs = a.Bsums ?? new short[1];
                    fixed (short* bp = bs)
                        for (int r = 0; r < rows; r++)
                        {
                            byte* src = p + r * (data.Length / rows);
                            Quants.DecodeRow(t, src, cols, rp);
                            for (int tok = 0; tok < T; tok++)
                            {
                                double want = 0, mag = 0, floatDot = 0;
                                for (int i = 0; i < cols; i++)
                                {
                                    double deq = (double)a.Q[tok * cols + i] * a.D[tok * nb + i / bsz];
                                    want += row[i] * deq; mag += Math.Abs(row[i] * deq);
                                    floatDot += (double)row[i] * x[tok * cols + i];
                                }
                                float got = FastMath.IntDot(t, src, qp + tok * cols, dp + tok * nb, a.K ? bp + tok * (cols / 16) : null, cols);
                                Check(Math.Abs(got - want) <= 1e-4 * mag + 1e-5, GgmlTypes.Name(t) + " row " + r + " token " + tok + ": integer " + got + " vs reference " + want);
                                Check(Math.Abs(got - floatDot) <= 0.03 * mag + 1e-4, GgmlTypes.Name(t) + " row " + r + " token " + tok + ": integer " + got + " is far from float " + floatDot);
                            }
                        }
                }
            }
        }

        // Whole models: integer dot products keep the logits close to the float path (the same bar the llama.cpp
        // comparison uses), pick the same top tokens, and a token's logits do not depend on batching.
        public static void IntegerDotModels()
        {
            string dir = Path.Combine(Path.GetTempPath(), "windlass-fastmath-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(dir);
            bool was = Transformer.IntDot;
            try
            {
                foreach (GgmlType t in new GgmlType[] { GgmlType.Q8_0, GgmlType.Q4_0, GgmlType.Q4_K, GgmlType.Q5_K, GgmlType.Q6_K })
                {
                    string path = Path.Combine(dir, GgmlTypes.Name(t) + ".gguf");
                    TinyModels.Write(path, new TinySpec { Arch = "llama", Type = t });
                    Transformer.IntDot = false;
                    float[] f = Run(path, TinyModels.ProbeTokens);
                    Transformer.IntDot = true;
                    float[] q = Run(path, TinyModels.ProbeTokens);
                    int V = f.Length / TinyModels.ProbeTokens.Length, T = TinyModels.ProbeTokens.Length, agree = 0;
                    double mean = 0, var = 0, maxDiff = 0;
                    for (int i = 0; i < V; i++) mean += f[(T - 1) * V + i];
                    mean /= V;
                    for (int i = 0; i < V; i++) var += (f[(T - 1) * V + i] - mean) * (f[(T - 1) * V + i] - mean);
                    double std = Math.Sqrt(var / V);
                    for (int i = 0; i < V; i++) maxDiff = Math.Max(maxDiff, Math.Abs(f[(T - 1) * V + i] - q[(T - 1) * V + i]));
                    for (int tok = 0; tok < T; tok++) if (Argmax(f, tok * V, V) == Argmax(q, tok * V, V)) agree++;
                    Check(maxDiff / std < 0.2, GgmlTypes.Name(t) + ": integer-dot logits differ by " + maxDiff + " (std " + std + ")");
                    Check(agree >= T - 1, GgmlTypes.Name(t) + ": top token agrees at only " + agree + "/" + T + " positions");

                    // One token at a time (the KV cache path) must match the whole-prompt pass, integer kernels on.
                    float[] stepped = RunStepwise(path, TinyModels.ProbeTokens);
                    for (int i = 0; i < stepped.Length; i++)
                        Check(Math.Abs(stepped[i] - q[(T - 1) * V + i]) <= 0.05 * std + 1e-3, GgmlTypes.Name(t) + ": token-by-token logits differ at " + i);
                }
            }
            finally
            {
                Transformer.IntDot = was;
                try { Directory.Delete(dir, true); } catch (IOException) { }
            }
        }

        static int Argmax(float[] a, int offset, int n)
        {
            int best = 0;
            for (int i = 1; i < n; i++) if (a[offset + i] > a[offset + best]) best = i;
            return best;
        }

        static float[] Run(string path, int[] tokens)
        {
            GgufModelFile g = GgufModelFile.Open(path);
            ModelConfig c = ModelConfig.From(g);
            using (LayerStream st = new LayerStream(g, c, true, 0))
                return new Transformer(g, c, st, 512).Forward(tokens, 0, true, CancellationToken.None, null);
        }

        static float[] RunStepwise(string path, int[] tokens)
        {
            GgufModelFile g = GgufModelFile.Open(path);
            ModelConfig c = ModelConfig.From(g);
            using (LayerStream st = new LayerStream(g, c, true, 0))
            {
                Transformer m = new Transformer(g, c, st, 512);
                float[] last = null;
                for (int i = 0; i < tokens.Length; i++) last = m.Forward(new int[] { tokens[i] }, i, false, CancellationToken.None, null);
                return last;
            }
        }
    }
}

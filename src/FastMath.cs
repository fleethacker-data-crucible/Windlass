// Faster matrix math for the CPU path. C# 5 only: scalar code and unsafe pointers.
//  - Block: register-blocked float dot products (4 weight rows x 2 prompt tokens per pass), used for prompts after a
//    tile of weight rows has been decoded.
//  - Integer dot products (Q8 activations): the activation row is quantized once, then each weight row is an integer
//    multiply-add, as in llama.cpp's vec_dot_q*_q8_* kernels (ggml-cpu, MIT).
// Every (row, token) result is a function of that row and that token only (fixed summation order), so a token's
// logits do not depend on how many other tokens ran beside it.
using System;

namespace Windlass
{
    public static unsafe class FastMath
    {
        // Quantized activations for T rows of Cols values. Kind Q8_0: blocks of 32, scale per block (fp16, as ggml).
        // Kind Q8_K: blocks of 256, float scale, plus sums of every 16 quants (bsums).
        public sealed class QActs
        {
            public bool K;                // Q8_K layout (256-wide blocks) when true, Q8_0 (32-wide) when false
            public int Rows, Cols;
            public sbyte[] Q;             // [Rows][Cols]
            public float[] D;             // [Rows][Cols / block]
            public short[] Bsums;         // [Rows][Cols / 16] (Q8_K only)
        }

        // Weight types that have an integer kernel, and the activation layout each one needs.
        public static bool CanIntDot(GgmlType t)
        {
            return t == GgmlType.Q8_0 || t == GgmlType.Q4_0 || t == GgmlType.Q4_K || t == GgmlType.Q5_K || t == GgmlType.Q6_K;
        }

        public static bool NeedsK(GgmlType t) { return t == GgmlType.Q4_K || t == GgmlType.Q5_K || t == GgmlType.Q6_K; }

        public static bool Fits(GgmlType t, int cols) { return cols % (NeedsK(t) ? 256 : 32) == 0; }

        // quantize_row_q8_0 / quantize_row_q8_K (reference versions).
        public static QActs Quantize(float[] x, int rows, int cols, bool k)
        {
            QActs a = new QActs();
            a.K = k; a.Rows = rows; a.Cols = cols;
            int bs = k ? 256 : 32, nb = cols / bs;
            a.Q = new sbyte[(long)rows * cols];
            a.D = new float[rows * nb];
            if (k) a.Bsums = new short[rows * (cols / 16)];
            fixed (float* xp = x) fixed (sbyte* qp = a.Q) fixed (float* dp = a.D)
            {
                for (int r = 0; r < rows; r++)
                {
                    float* xr = xp + (long)r * cols;
                    sbyte* qr = qp + (long)r * cols;
                    for (int b = 0; b < nb; b++)
                    {
                        float* xb = xr + b * bs;
                        sbyte* qb = qr + b * bs;
                        if (!k)
                        {
                            float amax = 0;
                            for (int j = 0; j < 32; j++) { float ax = Math.Abs(xb[j]); if (ax > amax) amax = ax; }
                            float d = amax / 127f, id = d != 0 ? 1f / d : 0f;
                            for (int j = 0; j < 32; j++) qb[j] = (sbyte)Math.Round(xb[j] * id, MidpointRounding.AwayFromZero);
                            dp[r * nb + b] = Half.Round(d);
                        }
                        else
                        {
                            float max = 0, amax = 0;
                            for (int j = 0; j < 256; j++) { float ax = Math.Abs(xb[j]); if (ax > amax) { amax = ax; max = xb[j]; } }
                            if (amax == 0)
                            {
                                dp[r * nb + b] = 0;
                                for (int j = 0; j < 256; j++) qb[j] = 0;
                                for (int j = 0; j < 16; j++) a.Bsums[r * (cols / 16) + b * 16 + j] = 0;
                                continue;
                            }
                            float iscale = -127f / max;
                            for (int j = 0; j < 256; j++)
                            {
                                int v = (int)Math.Round(iscale * xb[j]);
                                qb[j] = (sbyte)(v > 127 ? 127 : v);
                            }
                            for (int j = 0; j < 16; j++)
                            {
                                int s = 0;
                                for (int l = 0; l < 16; l++) s += qb[j * 16 + l];
                                a.Bsums[r * (cols / 16) + b * 16 + j] = (short)s;
                            }
                            dp[r * nb + b] = 1f / iscale;
                        }
                    }
                }
            }
            return a;
        }

        static float H(byte* p) { return Half.ToFloat(*(ushort*)p); }

        static void ScaleMinK4(int j, byte* q, out int sc, out int m)
        {
            if (j < 4) { sc = q[j] & 63; m = q[j + 4] & 63; }
            else
            {
                sc = (q[j + 4] & 0xF) | ((q[j - 4] >> 6) << 4);
                m = (q[j + 4] >> 4) | ((q[j] >> 6) << 4);
            }
        }

        // Dot of one weight row (n values in GGML type t at b) with activation row `row` of a. Pointers are pinned by
        // the caller for the duration of the call (the arrays inside a stay referenced).
        public static float IntDot(GgmlType t, byte* b, sbyte* q, float* d, short* bsums, int n)
        {
            switch (t)
            {
                case GgmlType.Q8_0:
                {
                    float sum = 0;
                    for (int i = 0; i < n / 32; i++, b += 34, q += 32)
                    {
                        sbyte* w = (sbyte*)(b + 2);
                        int s0 = 0, s1 = 0;
                        for (int j = 0; j < 32; j += 2) { s0 += w[j] * q[j]; s1 += w[j + 1] * q[j + 1]; }
                        sum += H(b) * d[i] * (s0 + s1);
                    }
                    return sum;
                }
                case GgmlType.Q4_0:
                {
                    float sum = 0;
                    for (int i = 0; i < n / 32; i++, b += 18, q += 32)
                    {
                        byte* w = b + 2;
                        int s0 = 0, s1 = 0;
                        for (int j = 0; j < 16; j++) { s0 += ((w[j] & 0xF) - 8) * q[j]; s1 += ((w[j] >> 4) - 8) * q[j + 16]; }
                        sum += H(b) * d[i] * (s0 + s1);
                    }
                    return sum;
                }
                case GgmlType.Q4_K:
                {
                    float sum = 0;
                    for (int i = 0; i < n / 256; i++, b += 144, q += 256, bsums += 16)
                    {
                        float dd = H(b), dmin = H(b + 2);
                        byte* sc = b + 4; byte* w = b + 16;
                        int sumi = 0, summ = 0;
                        for (int j = 0; j < 4; j++, w += 32)
                        {
                            int s1, m1, s2, m2;
                            ScaleMinK4(2 * j, sc, out s1, out m1);
                            ScaleMinK4(2 * j + 1, sc, out s2, out m2);
                            sbyte* qa = q + 64 * j; sbyte* qb = qa + 32;
                            int a1 = 0, a2 = 0;
                            for (int l = 0; l < 32; l++) { a1 += (w[l] & 0xF) * qa[l]; a2 += (w[l] >> 4) * qb[l]; }
                            sumi += s1 * a1 + s2 * a2;
                            summ += m1 * (bsums[4 * j] + bsums[4 * j + 1]) + m2 * (bsums[4 * j + 2] + bsums[4 * j + 3]);
                        }
                        sum += d[i] * (dd * sumi - dmin * summ);
                    }
                    return sum;
                }
                case GgmlType.Q5_K:
                {
                    float sum = 0;
                    for (int i = 0; i < n / 256; i++, b += 176, q += 256, bsums += 16)
                    {
                        float dd = H(b), dmin = H(b + 2);
                        byte* sc = b + 4; byte* qh = b + 16; byte* ql = b + 48;
                        int sumi = 0, summ = 0, u1 = 1, u2 = 2;
                        for (int j = 0; j < 4; j++, ql += 32, u1 <<= 2, u2 <<= 2)
                        {
                            int s1, m1, s2, m2;
                            ScaleMinK4(2 * j, sc, out s1, out m1);
                            ScaleMinK4(2 * j + 1, sc, out s2, out m2);
                            sbyte* qa = q + 64 * j; sbyte* qb = qa + 32;
                            int a1 = 0, a2 = 0;
                            for (int l = 0; l < 32; l++)
                            {
                                a1 += ((ql[l] & 0xF) + ((qh[l] & u1) != 0 ? 16 : 0)) * qa[l];
                                a2 += ((ql[l] >> 4) + ((qh[l] & u2) != 0 ? 16 : 0)) * qb[l];
                            }
                            sumi += s1 * a1 + s2 * a2;
                            summ += m1 * (bsums[4 * j] + bsums[4 * j + 1]) + m2 * (bsums[4 * j + 2] + bsums[4 * j + 3]);
                        }
                        sum += d[i] * (dd * sumi - dmin * summ);
                    }
                    return sum;
                }
                case GgmlType.Q6_K:
                {
                    float sum = 0;
                    int* acc = stackalloc int[8];
                    for (int i = 0; i < n / 256; i++, b += 210, q += 256)
                    {
                        float dd = H(b + 208);
                        byte* ql = b; byte* qh = b + 128; sbyte* sc = (sbyte*)(b + 192);
                        sbyte* x = q;
                        for (int k = 0; k < 2; k++, ql += 64, qh += 32, sc += 8, x += 128)
                        {
                            for (int g = 0; g < 8; g++) acc[g] = 0;
                            for (int l = 0; l < 32; l++)
                            {
                                int s = l >> 4;
                                acc[s] += (((ql[l] & 0xF) | ((qh[l] & 3) << 4)) - 32) * x[l];
                                acc[s + 2] += (((ql[l + 32] & 0xF) | (((qh[l] >> 2) & 3) << 4)) - 32) * x[l + 32];
                                acc[s + 4] += (((ql[l] >> 4) | (((qh[l] >> 4) & 3) << 4)) - 32) * x[l + 64];
                                acc[s + 6] += (((ql[l + 32] >> 4) | (((qh[l] >> 6) & 3) << 4)) - 32) * x[l + 96];
                            }
                            int part = 0;
                            for (int g = 0; g < 8; g++) part += sc[g] * acc[g];
                            sum += d[i] * dd * part;
                        }
                    }
                    return sum;
                }
            }
            throw new NotSupportedException("No integer dot for " + GgmlTypes.Name(t) + ".");
        }

        // y[t * yStride + r] = dot(W[r], X[t]) for nr weight rows (consecutive rows, w + r * ws floats) and nt input rows
        // (x + t * xs floats), each with cols values. One accumulator chain per (r, t), summed in index order.
        public static void Block(float* w, int ws, int nr, float* x, int xs, int nt, int cols, float* y, int yStride)
        {
            int r = 0;
            for (; r + 4 <= nr; r += 4)
            {
                float* w0 = w + (long)r * ws, w1 = w0 + ws, w2 = w1 + ws, w3 = w2 + ws;
                int t = 0;
                for (; t + 2 <= nt; t += 2)
                {
                    float* xa = x + (long)t * xs, xb = xa + xs;
                    float a0 = 0, a1 = 0, a2 = 0, a3 = 0, b0 = 0, b1 = 0, b2 = 0, b3 = 0;
                    for (int i = 0; i < cols; i++)
                    {
                        float va = xa[i], vb = xb[i];
                        float u0 = w0[i], u1 = w1[i], u2 = w2[i], u3 = w3[i];
                        a0 += u0 * va; a1 += u1 * va; a2 += u2 * va; a3 += u3 * va;
                        b0 += u0 * vb; b1 += u1 * vb; b2 += u2 * vb; b3 += u3 * vb;
                    }
                    float* ya = y + (long)t * yStride + r, yb = ya + yStride;
                    ya[0] = a0; ya[1] = a1; ya[2] = a2; ya[3] = a3;
                    yb[0] = b0; yb[1] = b1; yb[2] = b2; yb[3] = b3;
                }
                for (; t < nt; t++)
                {
                    float* xa = x + (long)t * xs;
                    float a0 = 0, a1 = 0, a2 = 0, a3 = 0;
                    for (int i = 0; i < cols; i++)
                    {
                        float va = xa[i];
                        a0 += w0[i] * va; a1 += w1[i] * va; a2 += w2[i] * va; a3 += w3[i] * va;
                    }
                    float* ya = y + (long)t * yStride + r;
                    ya[0] = a0; ya[1] = a1; ya[2] = a2; ya[3] = a3;
                }
            }
            for (; r < nr; r++)
            {
                float* wr = w + (long)r * ws;
                for (int t = 0; t < nt; t++)
                {
                    float* xa = x + (long)t * xs;
                    float s = 0;
                    for (int i = 0; i < cols; i++) s += wr[i] * xa[i];
                    y[(long)t * yStride + r] = s;
                }
            }
        }
    }
}

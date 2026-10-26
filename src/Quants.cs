// Weight decoding: one row of any supported GGML type to float. C# 5 only.
// Ported from the dequantize_row_* reference functions in ggml-quants.c (MIT, llama.cpp).
using System;

namespace Windlass
{
    public static unsafe class Quants
    {
        public const int QK = 32, QK_K = 256;

        public static bool CanDecode(GgmlType t)
        {
            switch (t)
            {
                case GgmlType.F32: case GgmlType.F16: case GgmlType.BF16:
                case GgmlType.Q4_0: case GgmlType.Q4_1: case GgmlType.Q5_0: case GgmlType.Q5_1: case GgmlType.Q8_0:
                case GgmlType.Q2_K: case GgmlType.Q3_K: case GgmlType.Q4_K: case GgmlType.Q5_K: case GgmlType.Q6_K:
                    return true;
            }
            return false;
        }

        public static float Bf16(ushort v)
        {
            uint bits = (uint)v << 16;
            return *(float*)&bits;
        }

        static float H(byte* p) { return Half.ToFloat(*(ushort*)p); }

        // Decodes n elements (a whole row) starting at src into dst.
        public static void DecodeRow(GgmlType t, byte* src, int n, float* dst)
        {
            switch (t)
            {
                case GgmlType.F32: for (int i = 0; i < n; i++) dst[i] = ((float*)src)[i]; return;
                case GgmlType.F16: for (int i = 0; i < n; i++) dst[i] = Half.ToFloat(((ushort*)src)[i]); return;
                case GgmlType.BF16: for (int i = 0; i < n; i++) dst[i] = Bf16(((ushort*)src)[i]); return;
                case GgmlType.Q4_0: Q4_0(src, n, dst); return;
                case GgmlType.Q4_1: Q4_1(src, n, dst); return;
                case GgmlType.Q5_0: Q5_0(src, n, dst); return;
                case GgmlType.Q5_1: Q5_1(src, n, dst); return;
                case GgmlType.Q8_0: Q8_0(src, n, dst); return;
                case GgmlType.Q2_K: Q2_K(src, n, dst); return;
                case GgmlType.Q3_K: Q3_K(src, n, dst); return;
                case GgmlType.Q4_K: Q4_K(src, n, dst); return;
                case GgmlType.Q5_K: Q5_K(src, n, dst); return;
                case GgmlType.Q6_K: Q6_K(src, n, dst); return;
            }
            throw new NotSupportedException("Windlass cannot decode " + GgmlTypes.Name(t) + " weights yet.");
        }

        // Fused decode + dot product for one row, used when a single token runs (every generated token): no scratch
        // row is written. Covers the types big GGUF files are made of; the rest decode first (CanDot false).
        public static bool CanDot(GgmlType t)
        {
            switch (t)
            {
                case GgmlType.F32: case GgmlType.F16: case GgmlType.BF16: case GgmlType.Q4_0: case GgmlType.Q8_0:
                case GgmlType.Q4_K: case GgmlType.Q5_K: case GgmlType.Q6_K:
                    return true;
            }
            return false;
        }

        public static float DotRow(GgmlType t, byte* b, float* x, int n)
        {
            switch (t)
            {
                case GgmlType.F32: return Transformer.Dot((float*)b, x, n);
                case GgmlType.F16:
                {
                    ushort* h = (ushort*)b;
                    float s0 = 0, s1 = 0;
                    for (int i = 0; i < n; i += 2) { s0 += Half.ToFloat(h[i]) * x[i]; s1 += Half.ToFloat(h[i + 1]) * x[i + 1]; }
                    return s0 + s1;
                }
                case GgmlType.BF16:
                {
                    ushort* h = (ushort*)b;
                    float s0 = 0, s1 = 0;
                    for (int i = 0; i < n; i += 2) { s0 += Bf16(h[i]) * x[i]; s1 += Bf16(h[i + 1]) * x[i + 1]; }
                    return s0 + s1;
                }
                case GgmlType.Q8_0:
                {
                    float sum = 0;
                    for (int i = 0; i < n / QK; i++, b += 34, x += QK)
                    {
                        sbyte* q = (sbyte*)(b + 2);
                        float s0 = 0, s1 = 0;
                        for (int j = 0; j < QK; j += 2) { s0 += q[j] * x[j]; s1 += q[j + 1] * x[j + 1]; }
                        sum += H(b) * (s0 + s1);
                    }
                    return sum;
                }
                case GgmlType.Q4_0:
                {
                    float sum = 0;
                    for (int i = 0; i < n / QK; i++, b += 18, x += QK)
                    {
                        byte* q = b + 2;
                        float s0 = 0, s1 = 0;
                        for (int j = 0; j < 16; j++) { s0 += ((q[j] & 0xF) - 8) * x[j]; s1 += ((q[j] >> 4) - 8) * x[j + 16]; }
                        sum += H(b) * (s0 + s1);
                    }
                    return sum;
                }
                case GgmlType.Q4_K:
                {
                    float sum = 0;
                    for (int i = 0; i < n / QK_K; i++, b += 144)
                    {
                        float d = H(b), min = H(b + 2);
                        byte* sc = b + 4; byte* q = b + 16;
                        for (int j = 0; j < 4; j++, q += 32, x += 64)
                        {
                            int s1, m1, s2, m2;
                            ScaleMinK4(2 * j, sc, out s1, out m1);
                            ScaleMinK4(2 * j + 1, sc, out s2, out m2);
                            float a1 = 0, a2 = 0, x1 = 0, x2 = 0;
                            for (int l = 0; l < 32; l++)
                            {
                                float xl = x[l], xh = x[l + 32];
                                a1 += (q[l] & 0xF) * xl; x1 += xl;
                                a2 += (q[l] >> 4) * xh; x2 += xh;
                            }
                            sum += d * (s1 * a1 + s2 * a2) - min * (m1 * x1 + m2 * x2);
                        }
                    }
                    return sum;
                }
                case GgmlType.Q5_K:
                {
                    float sum = 0;
                    for (int i = 0; i < n / QK_K; i++, b += 176)
                    {
                        float d = H(b), min = H(b + 2);
                        byte* sc = b + 4; byte* qh = b + 16; byte* ql = b + 48;
                        int u1 = 1, u2 = 2;
                        for (int j = 0; j < 4; j++, ql += 32, x += 64, u1 <<= 2, u2 <<= 2)
                        {
                            int s1, m1, s2, m2;
                            ScaleMinK4(2 * j, sc, out s1, out m1);
                            ScaleMinK4(2 * j + 1, sc, out s2, out m2);
                            float a1 = 0, a2 = 0, x1 = 0, x2 = 0;
                            for (int l = 0; l < 32; l++)
                            {
                                float xl = x[l], xh = x[l + 32];
                                a1 += ((ql[l] & 0xF) + ((qh[l] & u1) != 0 ? 16 : 0)) * xl; x1 += xl;
                                a2 += ((ql[l] >> 4) + ((qh[l] & u2) != 0 ? 16 : 0)) * xh; x2 += xh;
                            }
                            sum += d * (s1 * a1 + s2 * a2) - min * (m1 * x1 + m2 * x2);
                        }
                    }
                    return sum;
                }
                case GgmlType.Q6_K:
                {
                    float sum = 0;
                    float* acc = stackalloc float[8];
                    for (int i = 0; i < n / QK_K; i++, b += 210)
                    {
                        float d = H(b + 208);
                        byte* ql = b; byte* qh = b + 128; sbyte* sc = (sbyte*)(b + 192);
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
                            float part = 0;
                            for (int g = 0; g < 8; g++) part += sc[g] * acc[g];
                            sum += d * part;
                        }
                    }
                    return sum;
                }
            }
            throw new NotSupportedException("No fused dot for " + GgmlTypes.Name(t) + ".");
        }

        static void Q4_0(byte* b, int n, float* y)
        {
            for (int i = 0; i < n / QK; i++, b += 18, y += QK)
            {
                float d = H(b);
                byte* q = b + 2;
                for (int j = 0; j < 16; j++)
                {
                    y[j] = ((q[j] & 0xF) - 8) * d;
                    y[j + 16] = ((q[j] >> 4) - 8) * d;
                }
            }
        }

        static void Q4_1(byte* b, int n, float* y)
        {
            for (int i = 0; i < n / QK; i++, b += 20, y += QK)
            {
                float d = H(b), m = H(b + 2);
                byte* q = b + 4;
                for (int j = 0; j < 16; j++)
                {
                    y[j] = (q[j] & 0xF) * d + m;
                    y[j + 16] = (q[j] >> 4) * d + m;
                }
            }
        }

        static void Q5_0(byte* b, int n, float* y)
        {
            for (int i = 0; i < n / QK; i++, b += 22, y += QK)
            {
                float d = H(b);
                uint qh = *(uint*)(b + 2);
                byte* q = b + 6;
                for (int j = 0; j < 16; j++)
                {
                    int h0 = (int)(((qh >> j) << 4) & 0x10);
                    int h1 = (int)((qh >> (j + 12)) & 0x10);
                    y[j] = (((q[j] & 0xF) | h0) - 16) * d;
                    y[j + 16] = (((q[j] >> 4) | h1) - 16) * d;
                }
            }
        }

        static void Q5_1(byte* b, int n, float* y)
        {
            for (int i = 0; i < n / QK; i++, b += 24, y += QK)
            {
                float d = H(b), m = H(b + 2);
                uint qh = *(uint*)(b + 4);
                byte* q = b + 8;
                for (int j = 0; j < 16; j++)
                {
                    int h0 = (int)(((qh >> j) << 4) & 0x10);
                    int h1 = (int)((qh >> (j + 12)) & 0x10);
                    y[j] = ((q[j] & 0xF) | h0) * d + m;
                    y[j + 16] = ((q[j] >> 4) | h1) * d + m;
                }
            }
        }

        static void Q8_0(byte* b, int n, float* y)
        {
            for (int i = 0; i < n / QK; i++, b += 34, y += QK)
            {
                float d = H(b);
                sbyte* q = (sbyte*)(b + 2);
                for (int j = 0; j < QK; j++) y[j] = q[j] * d;
            }
        }

        // block_q2_K: u8 scales[16]; u8 qs[64]; half d; half dmin
        static void Q2_K(byte* b, int n, float* y)
        {
            for (int i = 0; i < n / QK_K; i++, b += 84)
            {
                byte* sc = b; byte* q = b + 16;
                float d = H(b + 80), min = H(b + 82);
                int idx = 0;
                for (int k = 0; k < QK_K; k += 128)
                {
                    int shift = 0;
                    for (int j = 0; j < 4; j++)
                    {
                        int s = sc[idx++];
                        float dl = d * (s & 0xF), ml = min * (s >> 4);
                        for (int l = 0; l < 16; l++) *y++ = dl * ((q[l] >> shift) & 3) - ml;
                        s = sc[idx++];
                        dl = d * (s & 0xF); ml = min * (s >> 4);
                        for (int l = 0; l < 16; l++) *y++ = dl * ((q[l + 16] >> shift) & 3) - ml;
                        shift += 2;
                    }
                    q += 32;
                }
            }
        }

        // block_q3_K: u8 hmask[32]; u8 qs[64]; u8 scales[12]; half d
        static void Q3_K(byte* b, int n, float* y)
        {
            const uint kmask1 = 0x03030303, kmask2 = 0x0f0f0f0f;
            uint* aux = stackalloc uint[4];
            for (int i = 0; i < n / QK_K; i++, b += 110)
            {
                byte* hm = b; byte* q = b + 32; byte* s12 = b + 96;
                float dAll = H(b + 108);
                aux[0] = *(uint*)s12; aux[1] = *(uint*)(s12 + 4); aux[2] = *(uint*)(s12 + 8);
                uint tmp = aux[2];
                aux[2] = ((aux[0] >> 4) & kmask2) | (((tmp >> 4) & kmask1) << 4);
                aux[3] = ((aux[1] >> 4) & kmask2) | (((tmp >> 6) & kmask1) << 4);
                aux[0] = (aux[0] & kmask2) | (((tmp >> 0) & kmask1) << 4);
                aux[1] = (aux[1] & kmask2) | (((tmp >> 2) & kmask1) << 4);
                sbyte* scales = (sbyte*)aux;
                int idx = 0, m = 1;
                for (int k = 0; k < QK_K; k += 128)
                {
                    int shift = 0;
                    for (int j = 0; j < 4; j++)
                    {
                        float dl = dAll * (scales[idx++] - 32);
                        for (int l = 0; l < 16; l++) *y++ = dl * (((q[l] >> shift) & 3) - ((hm[l] & m) != 0 ? 0 : 4));
                        dl = dAll * (scales[idx++] - 32);
                        for (int l = 0; l < 16; l++) *y++ = dl * (((q[l + 16] >> shift) & 3) - ((hm[l + 16] & m) != 0 ? 0 : 4));
                        shift += 2;
                        m <<= 1;
                    }
                    q += 32;
                }
            }
        }

        // 6-bit scale and min for sub-block j of a Q4_K/Q5_K block (get_scale_min_k4).
        static void ScaleMinK4(int j, byte* q, out int sc, out int m)
        {
            if (j < 4) { sc = q[j] & 63; m = q[j + 4] & 63; }
            else
            {
                sc = (q[j + 4] & 0xF) | ((q[j - 4] >> 6) << 4);
                m = (q[j + 4] >> 4) | ((q[j] >> 6) << 4);
            }
        }

        // block_q4_K: half d; half dmin; u8 scales[12]; u8 qs[128]
        static void Q4_K(byte* b, int n, float* y)
        {
            for (int i = 0; i < n / QK_K; i++, b += 144)
            {
                float d = H(b), min = H(b + 2);
                byte* sc = b + 4; byte* q = b + 16;
                int idx = 0;
                for (int j = 0; j < QK_K; j += 64)
                {
                    int s, m;
                    ScaleMinK4(idx, sc, out s, out m); float d1 = d * s, m1 = min * m;
                    ScaleMinK4(idx + 1, sc, out s, out m); float d2 = d * s, m2 = min * m;
                    for (int l = 0; l < 32; l++) *y++ = d1 * (q[l] & 0xF) - m1;
                    for (int l = 0; l < 32; l++) *y++ = d2 * (q[l] >> 4) - m2;
                    q += 32; idx += 2;
                }
            }
        }

        // block_q5_K: half d; half dmin; u8 scales[12]; u8 qh[32]; u8 qs[128]
        static void Q5_K(byte* b, int n, float* y)
        {
            for (int i = 0; i < n / QK_K; i++, b += 176)
            {
                float d = H(b), min = H(b + 2);
                byte* sc = b + 4; byte* qh = b + 16; byte* ql = b + 48;
                int idx = 0, u1 = 1, u2 = 2;
                for (int j = 0; j < QK_K; j += 64)
                {
                    int s, m;
                    ScaleMinK4(idx, sc, out s, out m); float d1 = d * s, m1 = min * m;
                    ScaleMinK4(idx + 1, sc, out s, out m); float d2 = d * s, m2 = min * m;
                    for (int l = 0; l < 32; l++) *y++ = d1 * ((ql[l] & 0xF) + ((qh[l] & u1) != 0 ? 16 : 0)) - m1;
                    for (int l = 0; l < 32; l++) *y++ = d2 * ((ql[l] >> 4) + ((qh[l] & u2) != 0 ? 16 : 0)) - m2;
                    ql += 32; idx += 2; u1 <<= 2; u2 <<= 2;
                }
            }
        }

        // block_q6_K: u8 ql[128]; u8 qh[64]; i8 scales[16]; half d
        static void Q6_K(byte* b, int n, float* y)
        {
            for (int i = 0; i < n / QK_K; i++, b += 210)
            {
                float d = H(b + 208);
                byte* ql = b; byte* qh = b + 128; sbyte* sc = (sbyte*)(b + 192);
                for (int k = 0; k < QK_K; k += 128)
                {
                    for (int l = 0; l < 32; l++)
                    {
                        int s = l / 16;
                        int q1 = ((ql[l] & 0xF) | (((qh[l] >> 0) & 3) << 4)) - 32;
                        int q2 = ((ql[l + 32] & 0xF) | (((qh[l] >> 2) & 3) << 4)) - 32;
                        int q3 = ((ql[l] >> 4) | (((qh[l] >> 4) & 3) << 4)) - 32;
                        int q4 = ((ql[l + 32] >> 4) | (((qh[l] >> 6) & 3) << 4)) - 32;
                        y[l] = d * sc[s] * q1;
                        y[l + 32] = d * sc[s + 2] * q2;
                        y[l + 64] = d * sc[s + 4] * q3;
                        y[l + 96] = d * sc[s + 6] * q4;
                    }
                    y += 128; ql += 64; qh += 32; sc += 8;
                }
            }
        }
    }
}

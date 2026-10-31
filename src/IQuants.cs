// Weight decoding for the I-quants: IQ4_NL, IQ4_XS, IQ3_XXS, IQ3_S, IQ2_XXS, IQ2_XS, IQ2_S, IQ1_S and IQ1_M. C# 5 only.
// Ported from the dequantize_row_iq* reference functions in ggml-quants.c (MIT, llama.cpp); the grids and sign tables are in IqTables.cs.
// There is no fused dot for these types (Quants.CanDot is false): single tokens decode a row and then dot it, like Q2_K and Q3_K.
using System;

namespace Windlass
{
    public static unsafe class IQuants
    {
        const int QK_K = 256;
        const float IQ1S_DELTA = 0.125f;

        public static bool Handles(GgmlType t)
        {
            switch (t)
            {
                case GgmlType.IQ4_NL: case GgmlType.IQ4_XS: case GgmlType.IQ3_XXS: case GgmlType.IQ3_S:
                case GgmlType.IQ2_XXS: case GgmlType.IQ2_XS: case GgmlType.IQ2_S: case GgmlType.IQ1_S: case GgmlType.IQ1_M:
                    return true;
            }
            return false;
        }

        static float H(byte* p) { return Half.ToFloat(*(ushort*)p); }

        // Decodes n elements (a whole row) starting at src into dst. n is a multiple of the block size (32 for IQ4_NL, else 256).
        public static void DecodeRow(GgmlType t, byte* src, int n, float* dst)
        {
            switch (t)
            {
                case GgmlType.IQ4_NL: IQ4_NL(src, n, dst); return;
                case GgmlType.IQ4_XS: IQ4_XS(src, n, dst); return;
                case GgmlType.IQ3_XXS: IQ3_XXS(src, n, dst); return;
                case GgmlType.IQ3_S: IQ3_S(src, n, dst); return;
                case GgmlType.IQ2_XXS: IQ2_XXS(src, n, dst); return;
                case GgmlType.IQ2_XS: IQ2_XS(src, n, dst); return;
                case GgmlType.IQ2_S: IQ2_S(src, n, dst); return;
                case GgmlType.IQ1_S: IQ1_S(src, n, dst); return;
                case GgmlType.IQ1_M: IQ1_M(src, n, dst); return;
            }
            throw new NotSupportedException("Windlass cannot decode " + GgmlTypes.Name(t) + " weights yet.");
        }

        // block_iq4_nl: half d; u8 qs[16]. 32 values; each nibble indexes kvalues_iq4nl.
        static void IQ4_NL(byte* b, int n, float* y)
        {
            sbyte[] kv = IqTables.KvaluesIq4nl;
            for (int i = 0; i < n / 32; i++, b += 18, y += 32)
            {
                float d = H(b);
                byte* q = b + 2;
                for (int j = 0; j < 16; j++)
                {
                    y[j] = d * kv[q[j] & 0xF];
                    y[j + 16] = d * kv[q[j] >> 4];
                }
            }
        }

        // block_iq4_xs: half d; u16 scales_h; u8 scales_l[4]; u8 qs[128]. Eight sub-blocks of 32 with 6-bit scales (minus 32).
        static void IQ4_XS(byte* b, int n, float* y)
        {
            sbyte[] kv = IqTables.KvaluesIq4nl;
            for (int i = 0; i < n / QK_K; i++, b += 136)
            {
                float d = H(b);
                int scalesH = *(ushort*)(b + 2);
                byte* scalesL = b + 4; byte* q = b + 8;
                for (int ib = 0; ib < QK_K / 32; ib++, q += 16, y += 32)
                {
                    int ls = ((scalesL[ib / 2] >> (4 * (ib % 2))) & 0xF) | (((scalesH >> (2 * ib)) & 3) << 4);
                    float dl = d * (ls - 32);
                    for (int j = 0; j < 16; j++)
                    {
                        y[j] = dl * kv[q[j] & 0xF];
                        y[j + 16] = dl * kv[q[j] >> 4];
                    }
                }
            }
        }

        // block_iq3_xxs: half d; u8 qs[96] = 64 grid indices, then per 32 values a u32 of four 7-bit sign indices and a 4-bit scale.
        static void IQ3_XXS(byte* b, int n, float* y)
        {
            uint[] grid = IqTables.Iq3xxsGrid;
            byte[] ksigns = IqTables.KsignsIq2xs, kmask = IqTables.KmaskIq2xs;
            for (int i = 0; i < n / QK_K; i++, b += 98)
            {
                float d = H(b);
                byte* qs = b + 2;
                byte* scalesAndSigns = qs + QK_K / 4;
                for (int ib32 = 0; ib32 < QK_K / 32; ib32++)
                {
                    uint aux32 = *(uint*)(scalesAndSigns + 4 * ib32);
                    float db = d * (0.5f + (aux32 >> 28)) * 0.5f;
                    for (int l = 0; l < 4; l++)
                    {
                        int signs = ksigns[(aux32 >> (7 * l)) & 127];
                        uint grid1 = grid[qs[2 * l + 0]], grid2 = grid[qs[2 * l + 1]];
                        for (int j = 0; j < 4; j++)
                        {
                            y[j + 0] = db * (byte)(grid1 >> (8 * j)) * ((signs & kmask[j + 0]) != 0 ? -1f : 1f);
                            y[j + 4] = db * (byte)(grid2 >> (8 * j)) * ((signs & kmask[j + 4]) != 0 ? -1f : 1f);
                        }
                        y += 8;
                    }
                    qs += 8;
                }
            }
        }

        // block_iq3_s: half d; u8 qs[64]; u8 qh[8]; u8 signs[32]; u8 scales[4]. Grid index is 9 bits (qs plus one bit of qh).
        static void IQ3_S(byte* b, int n, float* y)
        {
            uint[] grid = IqTables.Iq3sGrid;
            byte[] kmask = IqTables.KmaskIq2xs;
            for (int i = 0; i < n / QK_K; i++, b += 110)
            {
                float d = H(b);
                byte* qs = b + 2; byte* qh = b + 66; byte* signs = b + 74; byte* scales = b + 106;
                for (int ib32 = 0; ib32 < QK_K / 32; ib32 += 2)
                {
                    float db1 = d * (1 + 2 * (scales[ib32 / 2] & 0xF));
                    float db2 = d * (1 + 2 * (scales[ib32 / 2] >> 4));
                    for (int half = 0; half < 2; half++)
                    {
                        float db = half == 0 ? db1 : db2;
                        int h = qh[half];
                        for (int l = 0; l < 4; l++)
                        {
                            uint grid1 = grid[qs[2 * l + 0] | ((h << (8 - 2 * l)) & 256)];
                            uint grid2 = grid[qs[2 * l + 1] | ((h << (7 - 2 * l)) & 256)];
                            for (int j = 0; j < 4; j++)
                            {
                                y[j + 0] = db * (byte)(grid1 >> (8 * j)) * ((signs[l] & kmask[j + 0]) != 0 ? -1f : 1f);
                                y[j + 4] = db * (byte)(grid2 >> (8 * j)) * ((signs[l] & kmask[j + 4]) != 0 ? -1f : 1f);
                            }
                            y += 8;
                        }
                        qs += 8; signs += 4;
                    }
                    qh += 2;
                }
            }
        }

        // block_iq2_xxs: half d; u16 qs[32]. Per 32 values: four grid bytes, then a u32 of four 7-bit sign indices and a 4-bit scale.
        static void IQ2_XXS(byte* b, int n, float* y)
        {
            ulong[] grid = IqTables.Iq2xxsGrid;
            byte[] ksigns = IqTables.KsignsIq2xs, kmask = IqTables.KmaskIq2xs;
            for (int i = 0; i < n / QK_K; i++, b += 66)
            {
                float d = H(b);
                byte* qs = b + 2;
                for (int ib32 = 0; ib32 < QK_K / 32; ib32++)
                {
                    uint aux0 = *(uint*)(qs + 8 * ib32), aux1 = *(uint*)(qs + 8 * ib32 + 4);
                    float db = d * (0.5f + (aux1 >> 28)) * 0.25f;
                    for (int l = 0; l < 4; l++)
                    {
                        ulong g = grid[(aux0 >> (8 * l)) & 0xFF];
                        int signs = ksigns[(aux1 >> (7 * l)) & 127];
                        for (int j = 0; j < 8; j++)
                            y[j] = db * (byte)(g >> (8 * j)) * ((signs & kmask[j]) != 0 ? -1f : 1f);
                        y += 8;
                    }
                }
            }
        }

        // block_iq2_xs: half d; u16 qs[32]; u8 scales[8]. Each u16: 9-bit grid index and 7-bit sign index.
        static void IQ2_XS(byte* b, int n, float* y)
        {
            ulong[] grid = IqTables.Iq2xsGrid;
            byte[] ksigns = IqTables.KsignsIq2xs, kmask = IqTables.KmaskIq2xs;
            for (int i = 0; i < n / QK_K; i++, b += 74)
            {
                float d = H(b);
                ushort* qs = (ushort*)(b + 2);
                byte* scales = b + 66;
                for (int ib32 = 0; ib32 < QK_K / 32; ib32++)
                {
                    float db0 = d * (0.5f + (scales[ib32] & 0xF)) * 0.25f;
                    float db1 = d * (0.5f + (scales[ib32] >> 4)) * 0.25f;
                    for (int l = 0; l < 4; l++)
                    {
                        ulong g = grid[qs[4 * ib32 + l] & 511];
                        int signs = ksigns[qs[4 * ib32 + l] >> 9];
                        float db = l < 2 ? db0 : db1;
                        for (int j = 0; j < 8; j++)
                            y[j] = db * (byte)(g >> (8 * j)) * ((signs & kmask[j]) != 0 ? -1f : 1f);
                        y += 8;
                    }
                }
            }
        }

        // block_iq2_s: half d; u8 qs[64] (32 grid bytes, then 32 sign bytes); u8 qh[8]; u8 scales[8]. Grid index is 10 bits.
        static void IQ2_S(byte* b, int n, float* y)
        {
            ulong[] grid = IqTables.Iq2sGrid;
            byte[] kmask = IqTables.KmaskIq2xs;
            for (int i = 0; i < n / QK_K; i++, b += 82)
            {
                float d = H(b);
                byte* qs = b + 2; byte* signs = qs + QK_K / 8; byte* qh = b + 66; byte* scales = b + 74;
                for (int ib32 = 0; ib32 < QK_K / 32; ib32++)
                {
                    float db0 = d * (0.5f + (scales[ib32] & 0xF)) * 0.25f;
                    float db1 = d * (0.5f + (scales[ib32] >> 4)) * 0.25f;
                    for (int l = 0; l < 4; l++)
                    {
                        float dl = l < 2 ? db0 : db1;
                        ulong g = grid[qs[l] | ((qh[ib32] << (8 - 2 * l)) & 0x300)];
                        for (int j = 0; j < 8; j++)
                            y[j] = dl * (byte)(g >> (8 * j)) * ((signs[l] & kmask[j]) != 0 ? -1f : 1f);
                        y += 8;
                    }
                    qs += 4; signs += 4;
                }
            }
        }

        // block_iq1_s: half d; u8 qs[32]; u16 qh[8]. Per 32 values a u16: three bits of grid index per group of 8, 3-bit scale, delta sign.
        static void IQ1_S(byte* b, int n, float* y)
        {
            ulong[] grid = IqTables.Iq1sGrid;
            for (int i = 0; i < n / QK_K; i++, b += 50)
            {
                float d = H(b);
                byte* qs = b + 2;
                ushort* qh = (ushort*)(b + 34);
                for (int ib = 0; ib < QK_K / 32; ib++)
                {
                    float dl = d * (2 * ((qh[ib] >> 12) & 7) + 1);
                    float delta = (qh[ib] & 0x8000) != 0 ? -IQ1S_DELTA : IQ1S_DELTA;
                    for (int l = 0; l < 4; l++)
                    {
                        ulong g = grid[qs[l] | (((qh[ib] >> (3 * l)) & 7) << 8)];
                        for (int j = 0; j < 8; j++) y[j] = dl * ((sbyte)(byte)(g >> (8 * j)) + delta);
                        y += 8;
                    }
                    qs += 4;
                }
            }
        }

        // block_iq1_m: u8 qs[32]; u8 qh[16]; u16 scales[4]. No d field: the fp16 super-scale is spread over the top nibbles of the four scale words.
        static void IQ1_M(byte* b, int n, float* y)
        {
            ulong[] grid = IqTables.Iq1sGrid;
            for (int i = 0; i < n / QK_K; i++, b += 56)
            {
                ushort* sc = (ushort*)(b + 48);
                ushort scale16 = (ushort)((sc[0] >> 12) | ((sc[1] >> 8) & 0x00F0) | ((sc[2] >> 4) & 0x0F00) | (sc[3] & 0xF000));
                float d = Half.ToFloat(scale16);
                byte* qs = b; byte* qh = b + 32;
                for (int ib = 0; ib < QK_K / 32; ib++)
                {
                    float dl1 = d * (2 * ((sc[ib / 2] >> (6 * (ib % 2) + 0)) & 7) + 1);
                    float dl2 = d * (2 * ((sc[ib / 2] >> (6 * (ib % 2) + 3)) & 7) + 1);
                    int i0 = qs[0] | ((qh[0] << 8) & 0x700), i1 = qs[1] | ((qh[0] << 4) & 0x700);
                    int i2 = qs[2] | ((qh[1] << 8) & 0x700), i3 = qs[3] | ((qh[1] << 4) & 0x700);
                    float d0 = (qh[0] & 0x08) != 0 ? -IQ1S_DELTA : IQ1S_DELTA, d1 = (qh[0] & 0x80) != 0 ? -IQ1S_DELTA : IQ1S_DELTA;
                    float d2 = (qh[1] & 0x08) != 0 ? -IQ1S_DELTA : IQ1S_DELTA, d3 = (qh[1] & 0x80) != 0 ? -IQ1S_DELTA : IQ1S_DELTA;
                    for (int j = 0; j < 8; j++) y[j] = dl1 * ((sbyte)(byte)(grid[i0] >> (8 * j)) + d0);
                    y += 8;
                    for (int j = 0; j < 8; j++) y[j] = dl1 * ((sbyte)(byte)(grid[i1] >> (8 * j)) + d1);
                    y += 8;
                    for (int j = 0; j < 8; j++) y[j] = dl2 * ((sbyte)(byte)(grid[i2] >> (8 * j)) + d2);
                    y += 8;
                    for (int j = 0; j < 8; j++) y[j] = dl2 * ((sbyte)(byte)(grid[i3] >> (8 * j)) + d3);
                    y += 8;
                    qs += 4; qh += 2;
                }
            }
        }
    }
}

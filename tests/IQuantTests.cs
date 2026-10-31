// Tests for the I-quant weight types (IQ4_NL, IQ4_XS, IQ3_XXS, IQ3_S, IQ2_XXS, IQ2_XS, IQ2_S, IQ1_S, IQ1_M). C# 5 only.
// llama.cpp is not available here, so nothing below is checked against it: the decoders are compared with an independent,
// element-by-element re-derivation of dequantize_row_iq* (written from the block layouts in ggml-common.h, not by calling or
// copying the code under test), and the engine tests check that the streamed, prefetched and resident paths agree exactly.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace Windlass.Tests
{
    public sealed class IQuantFailure : Exception { public IQuantFailure(string m) : base(m) { } }

    public static unsafe class IQuantTests
    {
        public static readonly GgmlType[] Types =
        {
            GgmlType.IQ4_NL, GgmlType.IQ4_XS, GgmlType.IQ3_XXS, GgmlType.IQ3_S, GgmlType.IQ2_XXS, GgmlType.IQ2_XS,
            GgmlType.IQ2_S, GgmlType.IQ1_S, GgmlType.IQ1_M
        };

        static void Check(bool ok, string what) { if (!ok) throw new IQuantFailure(what); }

        static string NewTemp()
        {
            string d = Path.Combine(Path.GetTempPath(), "windlass-iquant-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(d);
            return d;
        }

        static void Cleanup(string d) { try { Directory.Delete(d, true); } catch (Exception) { } }

        // ---------------- little helpers that do not use the code under test ----------------

        static int U16(byte[] b, int o) { return b[o] | (b[o + 1] << 8); }
        static uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }

        // IEEE half to double, from the bit fields (normal numbers and zero are all the tests use, but subnormals are handled).
        static double Fp16(int h)
        {
            int sign = (h >> 15) & 1, exp = (h >> 10) & 31, man = h & 1023;
            double v;
            if (exp == 0) v = man / 1024.0 * Math.Pow(2, -14);
            else v = (1 + man / 1024.0) * Math.Pow(2, exp - 15);
            return sign == 1 ? -v : v;
        }

        // fp16 bit patterns of values that are exact in half precision.
        static readonly int[] DBits = { 0x3A00 /* 0.75 */, 0x3E00 /* 1.5 */, 0x3800 /* 0.5 */, 0xB400 /* -0.25 */, 0x3C00 /* 1 */ };

        static readonly int[] KValues = { -127, -104, -83, -65, -49, -35, -22, -10, 1, 13, 25, 38, 53, 69, 89, 113 };

        // Byte j (little endian) of a 64-bit grid word, and of a 32-bit one.
        static int GridByte(ulong g, int j) { return (int)((g >> (8 * j)) & 0xFF); }
        static int GridByte32(uint g, int j) { return (int)((g >> (8 * j)) & 0xFF); }

        // The sign byte for a 7-bit sign index: the index plus an eighth bit that makes the count of set bits even.
        static int SignByte(int idx7)
        {
            int bits = 0;
            for (int i = 0; i < 7; i++) if (((idx7 >> i) & 1) != 0) bits++;
            return idx7 | ((bits & 1) << 7);
        }

        static double Sign(int signByte, int j) { return ((signByte >> j) & 1) != 0 ? -1.0 : 1.0; }

        // ---------------- independent reference: one element at a time ----------------

        // Elements per block and bytes per block, from the field lists in ggml-common.h.
        static int BlockElems(GgmlType t) { return t == GgmlType.IQ4_NL ? 32 : 256; }
        static int BlockBytes(GgmlType t)
        {
            switch (t)
            {
                case GgmlType.IQ4_NL: return 2 + 16;
                case GgmlType.IQ4_XS: return 2 + 2 + 4 + 128;
                case GgmlType.IQ3_XXS: return 2 + 3 * 256 / 8;
                case GgmlType.IQ3_S: return 2 + 256 / 4 + 256 / 32 + 256 / 8 + 256 / 64;
                case GgmlType.IQ2_XXS: return 2 + 2 * 32;
                case GgmlType.IQ2_XS: return 2 + 2 * 32 + 256 / 32;
                case GgmlType.IQ2_S: return 2 + 256 / 4 + 256 / 32 + 256 / 32;
                case GgmlType.IQ1_S: return 2 + 256 / 8 + 2 * 256 / 32;
                case GgmlType.IQ1_M: return 256 / 8 + 256 / 16 + 256 / 32;
            }
            throw new ArgumentException();
        }

        // Value of element e (0-based, within the block) of the block starting at byte o of b.
        static double RefElement(GgmlType t, byte[] b, int o, int e)
        {
            int group = e / 8, within = e % 8;      // groups of 8 values share a grid word
            int sub = e / 32;                       // 32-value sub-block
            int l = group % 4;                      // group number inside the sub-block
            switch (t)
            {
                case GgmlType.IQ4_NL:
                {
                    double d = Fp16(U16(b, o));
                    int bytePos = e % 16;
                    int nib = e < 16 ? (b[o + 2 + bytePos] & 15) : (b[o + 2 + bytePos] >> 4);
                    return d * KValues[nib];
                }
                case GgmlType.IQ4_XS:
                {
                    double d = Fp16(U16(b, o));
                    int scalesH = U16(b, o + 2);
                    int low4 = (b[o + 4 + sub / 2] >> ((sub % 2) * 4)) & 15;
                    int high2 = (scalesH >> (2 * sub)) & 3;
                    int ls = low4 + 16 * high2;
                    int inSub = e % 32;
                    int q = b[o + 8 + sub * 16 + inSub % 16];
                    int nib = inSub < 16 ? (q & 15) : (q >> 4);
                    return d * (ls - 32) * KValues[nib];
                }
                case GgmlType.IQ3_XXS:
                {
                    double d = Fp16(U16(b, o));
                    uint packed = U32(b, o + 2 + 64 + 4 * sub);        // 4 x 7 sign bits, then a 4-bit scale
                    double db = d * (0.5 + (packed >> 28)) * 0.5;
                    int signIdx = (int)((packed >> (7 * l)) & 127);
                    // two grid bytes per group of 8: the first gives values 0..3, the second values 4..7
                    int gi = within < 4 ? b[o + 2 + 2 * group] : b[o + 2 + 2 * group + 1];
                    int gv = GridByte32(IqTables.Iq3xxsGrid[gi], within % 4);
                    return db * gv * Sign(SignByte(signIdx), within);
                }
                case GgmlType.IQ3_S:
                {
                    double d = Fp16(U16(b, o));
                    int k = 2 * l + (within < 4 ? 0 : 1);               // which of the 8 qs bytes of the sub-block
                    int qhBit = (b[o + 66 + sub] >> k) & 1;
                    int gi = b[o + 2 + sub * 8 + k] | (qhBit << 8);
                    int gv = GridByte32(IqTables.Iq3sGrid[gi], within % 4);
                    int signByte = b[o + 74 + sub * 4 + l];             // stored in full, no parity trick
                    int sc = b[o + 106 + sub / 2];
                    int nib = sub % 2 == 0 ? (sc & 15) : (sc >> 4);
                    return d * (1 + 2 * nib) * gv * Sign(signByte, within);
                }
                case GgmlType.IQ2_XXS:
                {
                    double d = Fp16(U16(b, o));
                    uint aux1 = U32(b, o + 2 + 8 * sub + 4);
                    double db = d * (0.5 + (aux1 >> 28)) * 0.25;
                    int gi = b[o + 2 + 8 * sub + l];                    // one grid byte per group of 8
                    int signIdx = (int)((aux1 >> (7 * l)) & 127);
                    return db * GridByte(IqTables.Iq2xxsGrid[gi], within) * Sign(SignByte(signIdx), within);
                }
                case GgmlType.IQ2_XS:
                {
                    double d = Fp16(U16(b, o));
                    int q = U16(b, o + 2 + 2 * group);                  // 9 bits of grid index, 7 bits of sign index
                    int sc = b[o + 66 + sub];
                    int nib = l < 2 ? (sc & 15) : (sc >> 4);
                    double db = d * (0.5 + nib) * 0.25;
                    return db * GridByte(IqTables.Iq2xsGrid[q & 511], within) * Sign(SignByte(q >> 9), within);
                }
                case GgmlType.IQ2_S:
                {
                    double d = Fp16(U16(b, o));
                    int qhBits = (b[o + 66 + sub] >> (2 * l)) & 3;      // two high bits of the 10-bit grid index
                    int gi = b[o + 2 + group] | (qhBits << 8);
                    int signByte = b[o + 2 + 32 + group];               // the sign bytes follow the 32 grid bytes
                    int sc = b[o + 74 + sub];
                    int nib = l < 2 ? (sc & 15) : (sc >> 4);
                    double db = d * (0.5 + nib) * 0.25;
                    return db * GridByte(IqTables.Iq2sGrid[gi], within) * Sign(signByte, within);
                }
                case GgmlType.IQ1_S:
                {
                    double d = Fp16(U16(b, o));
                    int qh = U16(b, o + 34 + 2 * sub);
                    int gi = b[o + 2 + group] | (((qh >> (3 * l)) & 7) << 8);
                    double dl = d * (2 * ((qh >> 12) & 7) + 1);
                    double delta = (qh & 0x8000) != 0 ? -0.125 : 0.125;
                    return dl * ((sbyte)GridByte(IqTables.Iq1sGrid[gi], within) + delta);
                }
                case GgmlType.IQ1_M:
                {
                    // d: fp16 whose four nibbles sit in the top nibbles of the four scale words
                    int h = 0;
                    for (int k = 0; k < 4; k++) h |= (U16(b, o + 48 + 2 * k) >> 12) << (4 * k);
                    double d = Fp16(h);
                    int qhByte = b[o + 32 + 2 * sub + (l < 2 ? 0 : 1)];
                    int loBits = (l % 2 == 0) ? (qhByte & 7) : ((qhByte >> 4) & 7);
                    bool negative = (l % 2 == 0) ? (qhByte & 0x08) != 0 : (qhByte & 0x80) != 0;
                    int gi = b[o + group] | (loBits << 8);
                    int word = U16(b, o + 48 + 2 * (sub / 2));
                    int s3 = (word >> (6 * (sub % 2) + (l < 2 ? 0 : 3))) & 7;
                    double dl = d * (2 * s3 + 1);
                    double delta = negative ? -0.125 : 0.125;
                    return dl * ((sbyte)GridByte(IqTables.Iq1sGrid[gi], within) + delta);
                }
            }
            throw new ArgumentException();
        }

        // Puts the fp16 bit pattern h in the scale field of the block at o.
        static void SetD(GgmlType t, byte[] b, int o, int h)
        {
            if (t == GgmlType.IQ1_M)
            {
                for (int k = 0; k < 4; k++)
                {
                    int hi = b[o + 48 + 2 * k + 1];
                    b[o + 48 + 2 * k + 1] = (byte)((hi & 0x0F) | (((h >> (4 * k)) & 15) << 4));
                }
                return;
            }
            b[o] = (byte)h; b[o + 1] = (byte)(h >> 8);
        }

        // Random payload, with a chosen d in each block (random bytes alone would make d a random half, NaN included).
        static byte[] RandomBlocks(GgmlType t, int blocks, ulong seed)
        {
            Rng rng = new Rng(seed);
            int bb = BlockBytes(t);
            byte[] data = new byte[blocks * bb];
            for (int i = 0; i < data.Length; i++) data[i] = rng.Byte();
            for (int k = 0; k < blocks; k++) SetD(t, data, k * bb, DBits[k % DBits.Length]);
            return data;
        }

        static float[] Decode(GgmlType t, byte[] data, int n)
        {
            float[] y = new float[n];
            fixed (byte* p = data) fixed (float* d = y) Quants.DecodeRow(t, p, n, d);
            return y;
        }

        static void CompareBlocks(GgmlType t, byte[] data, int blocks, string what)
        {
            int be = BlockElems(t), bb = BlockBytes(t);
            float[] got = Decode(t, data, blocks * be);
            int bad = 0; string first = null;
            for (int k = 0; k < blocks; k++)
                for (int e = 0; e < be; e++)
                {
                    double want = RefElement(t, data, k * bb, e);
                    double g = got[k * be + e];
                    if (Math.Abs(g - want) > 2e-6 * Math.Abs(want) + 1e-9)
                    {
                        if (first == null) first = "block " + k + " element " + e + ": got " + g + ", expected " + want;
                        bad++;
                    }
                }
            Check(bad == 0, GgmlTypes.Name(t) + " " + what + ": " + bad + " of " + blocks * be + " values differ (" + first + ")");
        }

        // ---------------- tests ----------------

        // Table data: spot values written out from ggml-common.h, byte sets, sizes, distinctness and a hash of each table.
        public static void Tables()
        {
            Check(IqTables.Iq2xxsGrid.Length == 256 && IqTables.Iq2xsGrid.Length == 512 && IqTables.Iq2sGrid.Length == 1024, "iq2 grid sizes");
            Check(IqTables.Iq3xxsGrid.Length == 256 && IqTables.Iq3sGrid.Length == 512 && IqTables.Iq1sGrid.Length == 2048, "iq3/iq1 grid sizes");
            Check(IqTables.KmaskIq2xs.Length == 8 && IqTables.KsignsIq2xs.Length == 128 && IqTables.KvaluesIq4nl.Length == 16, "mask/sign/value table sizes");
            for (int i = 0; i < 8; i++) Check(IqTables.KmaskIq2xs[i] == (1 << i), "kmask_iq2xs[" + i + "]");
            for (int i = 0; i < 128; i++) Check(IqTables.KsignsIq2xs[i] == SignByte(i), "ksigns_iq2xs[" + i + "] is the index plus an even-parity bit");
            for (int i = 0; i < 16; i++) Check(IqTables.KvaluesIq4nl[i] == KValues[i], "kvalues_iq4nl[" + i + "]");
            Check(IqTables.Iq2xxsGrid[0] == 0x0808080808080808UL && IqTables.Iq2xxsGrid[1] == 0x080808080808082bUL && IqTables.Iq2xxsGrid[3] == 0x0808080808082b08UL, "iq2xxs grid start");
            Check(IqTables.Iq2xsGrid[0] == 0x0808080808080808UL && IqTables.Iq2xsGrid[1] == 0x080808080808082bUL && IqTables.Iq2xsGrid[2] == 0x0808080808081919UL
                && IqTables.Iq2xsGrid[511] == 0x2b2b2b2b2b2b2b2bUL, "iq2xs grid ends");
            Check(IqTables.Iq3xxsGrid[0] == 0x04040404u && IqTables.Iq3xxsGrid[1] == 0x04040414u && IqTables.Iq3xxsGrid[255] == 0x3e341c04u, "iq3xxs grid ends");
            Check(IqTables.Iq3sGrid[0] == 0x01010101u && IqTables.Iq3sGrid[1] == 0x01010103u && IqTables.Iq3sGrid[511] == 0x0f0f0101u, "iq3s grid ends");
            Check(IqTables.Iq1sGrid[0] == 0xffffffffffffffffUL && IqTables.Iq1sGrid[1] == 0xffffffffffffff01UL && IqTables.Iq1sGrid[2] == 0xffffffffffff0000UL
                && IqTables.Iq1sGrid[2047] == 0x0101010101010101UL, "iq1s grid ends");

            int[] two = { 8, 0x19, 0x2b }, threeXxs = { 4, 12, 20, 28, 36, 44, 52, 62 }, threeS = { 1, 3, 5, 7, 9, 11, 13, 15 }, one = { 0, 1, 255 };
            CheckBytes(IqTables.Iq2xxsGrid, two, 8, "iq2xxs");
            CheckBytes(IqTables.Iq2xsGrid, two, 8, "iq2xs");
            CheckBytes(IqTables.Iq2sGrid, two, 8, "iq2s");
            CheckBytes32(IqTables.Iq3xxsGrid, threeXxs, "iq3xxs");
            CheckBytes32(IqTables.Iq3sGrid, threeS, "iq3s");
            CheckBytes(IqTables.Iq1sGrid, one, 8, "iq1s");
            Check(Fnv64(IqTables.Iq2xxsGrid) == 0xbb4ee025b5ac6e8eUL, "iq2xxs grid hash");
            Check(Fnv64(IqTables.Iq2xsGrid) == 0xc9b1ee61e79909bdUL, "iq2xs grid hash");
            Check(Fnv64(IqTables.Iq2sGrid) == 0x123edd38a3b62b90UL, "iq2s grid hash");
            Check(Fnv64(IqTables.Iq3xxsGrid) == 0xdfa5dc83d6a155d5UL, "iq3xxs grid hash");
            Check(Fnv64(IqTables.Iq3sGrid) == 0xfa37020c25b44829UL, "iq3s grid hash");
            Check(Fnv64(IqTables.Iq1sGrid) == 0x6703ed863501ae2eUL, "iq1s grid hash");
        }

        static void CheckBytes(ulong[] grid, int[] allowed, int width, string name)
        {
            Dictionary<ulong, bool> seen = new Dictionary<ulong, bool>();
            foreach (ulong g in grid)
            {
                for (int j = 0; j < width; j++) Check(Array.IndexOf(allowed, GridByte(g, j)) >= 0, name + " grid byte " + GridByte(g, j) + " is not an allowed value");
                seen[g] = true;
            }
            Check(seen.Count == grid.Length, name + " grid entries are distinct");
        }

        static void CheckBytes32(uint[] grid, int[] allowed, string name)
        {
            Dictionary<uint, bool> seen = new Dictionary<uint, bool>();
            foreach (uint g in grid)
            {
                for (int j = 0; j < 4; j++) Check(Array.IndexOf(allowed, GridByte32(g, j)) >= 0, name + " grid byte " + GridByte32(g, j) + " is not an allowed value");
                seen[g] = true;
            }
            Check(seen.Count == grid.Length, name + " grid entries are distinct");
        }

        // FNV-1a over the little-endian bytes of the table.
        static ulong Fnv64(ulong[] t)
        {
            ulong h = 14695981039346656037UL;
            foreach (ulong v in t) for (int j = 0; j < 8; j++) { h ^= (byte)(v >> (8 * j)); h *= 1099511628211UL; }
            return h;
        }

        static ulong Fnv64(uint[] t)
        {
            ulong h = 14695981039346656037UL;
            foreach (uint v in t) for (int j = 0; j < 4; j++) { h ^= (byte)(v >> (8 * j)); h *= 1099511628211UL; }
            return h;
        }

        // Hand-built blocks with values worked out by hand, then random blocks against the element-by-element reference.
        public static void Decoders()
        {
            // IQ4_NL: d = 2, qs[j] = j | (15 - j) << 4, so y[j] = 2 * kvalues[j] and y[16 + j] = 2 * kvalues[15 - j].
            byte[] nl = new byte[18];
            nl[0] = 0x00; nl[1] = 0x40;                 // 2.0
            for (int j = 0; j < 16; j++) nl[2 + j] = (byte)(j | ((15 - j) << 4));
            float[] y = Decode(GgmlType.IQ4_NL, nl, 32);
            for (int j = 0; j < 16; j++)
            {
                Check(y[j] == 2f * KValues[j], "IQ4_NL hand block, low nibble " + j + ": " + y[j]);
                Check(y[16 + j] == 2f * KValues[15 - j], "IQ4_NL hand block, high nibble " + j + ": " + y[16 + j]);
            }

            // IQ2_XXS: d = 1. Sub-block 0 uses grid words 0..3 and sign indices 0, 1, 2, 127 with scale nibble 3 (db = 0.875).
            // Everything else is zero: grid word 0 (all 8), no signs, scale nibble 0 (db = 0.125), so those values are 1.0.
            byte[] xxs = new byte[66];
            xxs[0] = 0x00; xxs[1] = 0x3C;
            xxs[2] = 0; xxs[3] = 1; xxs[4] = 2; xxs[5] = 3;
            uint aux1 = (3u << 28) | (0u << 0) | (1u << 7) | (2u << 14) | (127u << 21);
            xxs[6] = (byte)aux1; xxs[7] = (byte)(aux1 >> 8); xxs[8] = (byte)(aux1 >> 16); xxs[9] = (byte)(aux1 >> 24);
            y = Decode(GgmlType.IQ2_XXS, xxs, 256);
            double[] want = new double[32];
            // group 0: grid 0x0808080808080808, sign byte 0 -> eight +7.0
            for (int j = 0; j < 8; j++) want[j] = 0.875 * 8;
            // group 1: grid 0x080808080808082b (byte 0 = 43), sign index 1 -> sign byte 129 (bits 0 and 7 negative)
            for (int j = 0; j < 8; j++) want[8 + j] = 0.875 * (j == 0 ? 43 : 8) * (j == 0 || j == 7 ? -1 : 1);
            // group 2: grid 0x0808080808081919 (bytes 0,1 = 25), sign index 2 -> sign byte 130 (bits 1 and 7 negative)
            for (int j = 0; j < 8; j++) want[16 + j] = 0.875 * (j < 2 ? 25 : 8) * (j == 1 || j == 7 ? -1 : 1);
            // group 3: grid 0x0808080808082b08 (byte 1 = 43), sign index 127 -> sign byte 255 (all negative)
            for (int j = 0; j < 8; j++) want[24 + j] = -0.875 * (j == 1 ? 43 : 8);
            for (int e = 0; e < 32; e++) Check(Math.Abs(y[e] - want[e]) < 1e-6, "IQ2_XXS hand block element " + e + ": " + y[e] + " vs " + want[e]);
            for (int e = 32; e < 256; e++) Check(Math.Abs(y[e] - 1.0) < 1e-6, "IQ2_XXS hand block element " + e + " should be 1.0, got " + y[e]);

            // Every type: blocks with random payloads, whole-row decode against the reference, including a row that is two blocks long
            // and a second call that starts in the middle of a buffer (so nothing depends on a block being the first one).
            foreach (GgmlType t in Types)
            {
                int blocks = t == GgmlType.IQ4_NL ? 16 : 5;
                CompareBlocks(t, RandomBlocks(t, blocks, 11), blocks, "random blocks");
                CompareBlocks(t, RandomBlocks(t, blocks, 12345), blocks, "random blocks (second seed)");
            }
            // Extremes the random draw can miss: every payload byte 0x00 and 0xFF.
            foreach (GgmlType t in Types)
                foreach (byte fill in new byte[] { 0x00, 0xFF })
                {
                    byte[] data = new byte[2 * BlockBytes(t)];
                    for (int i = 0; i < data.Length; i++) data[i] = fill;
                    SetD(t, data, 0, 0x3A00); SetD(t, data, BlockBytes(t), 0x3E00);
                    CompareBlocks(t, data, 2, "payload 0x" + fill.ToString("X2"));
                }
        }

        // Layout bookkeeping: block and row sizes, tensor sizes in a written file, the decode flags and the names.
        public static void Sizes()
        {
            // sizeof(block_*) in ggml-common.h
            int[] elems = { 32, 256, 256, 256, 256, 256, 256, 256, 256 };
            int[] bytes = { 18, 136, 98, 110, 66, 74, 82, 50, 56 };
            string[] names = { "iq4_nl", "iq4_xs", "iq3_xxs", "iq3_s", "iq2_xxs", "iq2_xs", "iq2_s", "iq1_s", "iq1_m" };
            int[] ids = { 20, 23, 18, 21, 16, 17, 22, 19, 29 };
            string dir = NewTemp();
            try
            {
                for (int i = 0; i < Types.Length; i++)
                {
                    GgmlType t = Types[i];
                    Check((int)t == ids[i], names[i] + " has the ggml_type number " + ids[i]);
                    int blockSize, typeSize;
                    Check(GgmlTypes.TryGetLayout(t, out blockSize, out typeSize), names[i] + " has a layout");
                    Check(blockSize == elems[i] && typeSize == bytes[i], names[i] + " layout " + blockSize + "/" + typeSize + ", expected " + elems[i] + "/" + bytes[i]);
                    Check(BlockElems(t) == elems[i] && BlockBytes(t) == bytes[i], names[i] + " field sizes add up to the block size");
                    Check(GgmlTypes.Name(t) == names[i], names[i] + " name is " + GgmlTypes.Name(t));
                    Check(Quants.CanDecode(t), names[i] + " can decode");
                    Check(!Quants.CanDot(t), names[i] + " has no fused dot (it decodes first)");

                    // a 3-row, 512-column tensor in a file
                    GgufWriter w = new GgufWriter();
                    w.Add("general.alignment", 32u);
                    byte[] data = RandomBlocks(t, 3 * 512 / elems[i], 5);
                    w.AddTensor("a.weight", t, data, 512, 3);
                    string path = Path.Combine(dir, names[i] + ".gguf");
                    w.Save(path);
                    GgufModelFile g = GgufModelFile.Open(path);
                    GgufTensor tensor = g.TensorsByName["a.weight"];
                    long rowBytes = 512 / elems[i] * bytes[i];
                    Check(tensor.Type == t && tensor.Rows == 3 && tensor.Cols == 512, names[i] + " tensor shape");
                    Check(tensor.SizeBytes == 3 * rowBytes && tensor.SizeBytes == data.Length, names[i] + " tensor size " + tensor.SizeBytes + ", expected " + 3 * rowBytes);
                    Check(tensor.SizeBytes / tensor.Rows == rowBytes, names[i] + " row bytes");

                    // a row that is not a whole number of blocks is refused
                    GgufWriter bad = new GgufWriter();
                    bad.Add("general.alignment", 32u);
                    int badCols = elems[i] + elems[i] / 2;
                    bad.AddTensor("b.weight", t, new byte[2 * (badCols / elems[i] + 1) * bytes[i]], badCols, 2);
                    string badPath = Path.Combine(dir, names[i] + "-bad.gguf");
                    bad.Save(badPath);
                    try { GgufModelFile.Open(badPath); throw new IQuantFailure(names[i] + " with a partial block per row must be refused"); }
                    catch (InvalidDataException) { }
                }
            }
            finally { Cleanup(dir); }
            Check(!Quants.CanDecode(GgmlType.Q8_K) && !Quants.CanDecode(GgmlType.Q8_1), "types without a decoder are still refused");
        }

        // The generic path the engine uses for these types (decode a tile of rows, then dot): one token and a batch of three.
        public static void MatMul()
        {
            foreach (GgmlType t in Types)
            {
                const int rows = 5, cols = 512;
                int be = BlockElems(t), bb = BlockBytes(t);
                byte[] data = RandomBlocks(t, rows * cols / be, 77);
                double[][] w = new double[rows][];
                for (int r = 0; r < rows; r++)
                {
                    w[r] = new double[cols];
                    for (int c = 0; c < cols; c++) w[r][c] = RefElement(t, data, (r * cols + c) / be * bb, (r * cols + c) % be);
                }
                foreach (int T in new int[] { 1, 3 })
                {
                    float[] x = new float[T * cols];
                    Rng rng = new Rng(8);
                    for (int i = 0; i < x.Length; i++) x[i] = rng.Normal();
                    float[] y = new float[T * rows];
                    fixed (byte* p = data)
                    {
                        WeightView v = new WeightView { Name = "test", Type = t, Rows = rows, Cols = cols, RowBytes = cols / be * bb, Data = p };
                        Transformer.MatMul(v, x, T, y);
                    }
                    for (int ti = 0; ti < T; ti++)
                        for (int r = 0; r < rows; r++)
                        {
                            double want = 0, mag = 0;
                            for (int c = 0; c < cols; c++) { want += w[r][c] * x[ti * cols + c]; mag += Math.Abs(w[r][c] * x[ti * cols + c]); }
                            Check(Math.Abs(y[ti * rows + r] - want) <= 1e-4 * mag + 1e-4, GgmlTypes.Name(t) + " T=" + T + " token " + ti + " row " + r + ": " + y[ti * rows + r] + " vs " + want);
                        }
                }
            }
        }

        static string Model(string dir, TinySpec s)
        {
            string p = Path.Combine(dir, s.Key + ".gguf");
            if (!File.Exists(p)) TinyModels.Write(p, s);
            return p;
        }

        static float[] Logits(string path, int[] tokens, bool prefetch, long cacheBytes, bool all)
        {
            GgufModelFile g = GgufModelFile.Open(path);
            ModelConfig c = ModelConfig.From(g);
            using (LayerStream st = new LayerStream(g, c, prefetch, cacheBytes))
                return new Transformer(g, c, st, 512).Forward(tokens, 0, all, CancellationToken.None, null);
        }

        static void Same(float[] a, float[] b, string what)
        {
            Check(a.Length == b.Length, what + " length");
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) throw new IQuantFailure(what + ": differ at " + i + " (" + a[i] + " vs " + b[i] + ")");
        }

        // A tiny Llama model in each I-quant type loads, runs, and gives bit-identical logits however its layers are streamed.
        public static void Streaming()
        {
            string dir = NewTemp();
            try
            {
                foreach (GgmlType t in Types)
                {
                    TinySpec s = new TinySpec { Arch = "llama", Type = t };
                    string p = Model(dir, s);
                    GgufModelFile g = GgufModelFile.Open(p);
                    ModelConfig c = ModelConfig.From(g);       // used to throw "cannot decode yet"
                    Check(g.TensorsByName["blk.0.ffn_down.weight"].Type == t && g.TensorsByName["token_embd.weight"].Type == t, s.Key + ": weights are " + GgmlTypes.Name(t));
                    float[] streamed = Logits(p, TinyModels.ProbeTokens, true, 0, true);
                    int V = c.Vocab, T = TinyModels.ProbeTokens.Length;
                    Check(streamed.Length == T * V, s.Key + " logits size");
                    double mean = 0, var = 0;
                    foreach (float v in streamed) { Check(!float.IsNaN(v) && !float.IsInfinity(v), s.Key + ": non-finite logit"); mean += v; }
                    mean /= streamed.Length;
                    foreach (float v in streamed) var += (v - mean) * (v - mean);
                    Check(var / streamed.Length > 1e-8, s.Key + ": logits are constant");
                    Same(streamed, Logits(p, TinyModels.ProbeTokens, false, 0, true), s.Key + " no prefetch");
                    Same(streamed, Logits(p, TinyModels.ProbeTokens, true, 1L << 40, true), s.Key + " all resident");
                    long oneLayer;
                    using (LayerStream probe = new LayerStream(g, c, true, 0)) oneLayer = probe.SlotBytes;
                    Same(streamed, Logits(p, TinyModels.ProbeTokens, true, oneLayer, true), s.Key + " first layer resident");
                }
            }
            finally { Cleanup(dir); }
        }

        // Token by token (and after Truncate) equals one full pass, for three types on three architectures.
        public static void KvCache()
        {
            string dir = NewTemp();
            try
            {
                foreach (TinySpec s in new TinySpec[] {
                    new TinySpec { Arch = "llama", Type = GgmlType.IQ4_XS },
                    new TinySpec { Arch = "qwen2", Type = GgmlType.IQ2_S, Tied = true },
                    new TinySpec { Arch = "qwen3", Type = GgmlType.IQ1_M } })
                {
                    string p = Model(dir, s);
                    int[] toks = TinyModels.ProbeTokens;
                    float[] full = Logits(p, toks, true, 0, false);
                    GgufModelFile g = GgufModelFile.Open(p);
                    ModelConfig c = ModelConfig.From(g);
                    using (LayerStream st = new LayerStream(g, c, true, 0))
                    {
                        Transformer tr = new Transformer(g, c, st, 512);
                        float[] step = tr.Forward(new int[] { toks[0], toks[1], toks[2], toks[3] }, 0, false, CancellationToken.None, null);
                        for (int i = 4; i < toks.Length; i++) step = tr.Forward(new int[] { toks[i] }, i, false, CancellationToken.None, null);
                        for (int i = 0; i < full.Length; i++)
                            Check(Math.Abs(full[i] - step[i]) < 1e-3, s.Key + ": token-by-token logits differ at " + i + " (" + full[i] + " vs " + step[i] + ")");
                        tr.Truncate(6);
                        float[] again = tr.Forward(new int[] { toks[6], toks[7], toks[8], toks[9], toks[10], toks[11] }, 6, false, CancellationToken.None, null);
                        for (int i = 0; i < full.Length; i++) Check(Math.Abs(full[i] - again[i]) < 1e-3, s.Key + ": logits after Truncate differ at " + i);
                    }
                }
            }
            finally { Cleanup(dir); }
        }
    }
}

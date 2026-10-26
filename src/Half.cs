// IEEE 754 half <-> float. .NET Framework 4.8 has no System.Half. C# 5 only. From Phoenix LAI (MIT).
// Matches GGML_FP16_TO_FP32 / GGML_FP32_TO_FP16 (round to nearest even) in llama.cpp.
namespace Windlass
{
    public static unsafe class Half
    {
        static readonly float[] Table = BuildTable();

        static float[] BuildTable()
        {
            float[] t = new float[65536];
            for (int i = 0; i < 65536; i++) t[i] = Decode((ushort)i);
            return t;
        }

        public static float ToFloat(ushort h)
        {
            return Table[h];
        }

        public static float Round(float f)
        {
            return Table[FromFloat(f)];
        }

        public static ushort FromFloat(float value)
        {
            uint f = *(uint*)&value;
            uint sign = (f >> 16) & 0x8000;
            int e = (int)((f >> 23) & 0xFF);
            uint m = f & 0x7FFFFF;

            if (e == 255) return (ushort)(sign | 0x7C00 | (m != 0 ? 0x200u : 0u));

            int ne = e - 127 + 15;
            if (ne >= 31) return (ushort)(sign | 0x7C00);

            if (ne <= 0)
            {
                if (ne < -10) return (ushort)sign;
                m |= 0x800000;
                int shift = 14 - ne;
                uint hm = m >> shift;
                uint rem = m & ((1u << shift) - 1);
                uint halfway = 1u << (shift - 1);
                if (rem > halfway || (rem == halfway && (hm & 1) != 0)) hm++;
                return (ushort)(sign | hm);
            }

            uint h = sign | ((uint)ne << 10) | (m >> 13);
            uint r = m & 0x1FFF;
            if (r > 0x1000 || (r == 0x1000 && (h & 1) != 0)) h++; // carry may roll into the exponent, which is correct
            return (ushort)h;
        }

        static float Decode(ushort h)
        {
            uint sign = (uint)(h >> 15) << 31;
            int exp = (h >> 10) & 0x1F;
            uint mant = (uint)(h & 0x3FF);
            uint bits;

            if (exp == 0)
            {
                if (mant == 0)
                {
                    bits = sign;
                }
                else
                {
                    int e = -1;
                    do { e++; mant <<= 1; } while ((mant & 0x400) == 0);
                    mant &= 0x3FF;
                    bits = sign | (uint)(127 - 15 - e) << 23 | mant << 13;
                }
            }
            else if (exp == 31)
            {
                bits = sign | 0x7F800000u | mant << 13;
            }
            else
            {
                bits = sign | (uint)(exp - 15 + 127) << 23 | mant << 13;
            }
            return *(float*)&bits;
        }
    }
}

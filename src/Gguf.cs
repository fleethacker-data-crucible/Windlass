// GGUF reader for layer streaming. C# 5 only.
// Reads only the header (metadata + tensor index); weights are read later, one layer at a time, by LayerStream.
// Split models (name-00001-of-00004.gguf) are opened as one. Format: gguf.md in llama.cpp (MIT).
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Windlass
{
    public enum GgufType
    {
        UInt8 = 0, Int8 = 1, UInt16 = 2, Int16 = 3, UInt32 = 4, Int32 = 5, Float32 = 6,
        Bool = 7, String = 8, Array = 9, UInt64 = 10, Int64 = 11, Float64 = 12
    }

    // Values match enum ggml_type in ggml.h.
    public enum GgmlType
    {
        F32 = 0, F16 = 1, Q4_0 = 2, Q4_1 = 3, Q5_0 = 6, Q5_1 = 7, Q8_0 = 8, Q8_1 = 9,
        Q2_K = 10, Q3_K = 11, Q4_K = 12, Q5_K = 13, Q6_K = 14, Q8_K = 15,
        I8 = 24, I16 = 25, I32 = 26, I64 = 27, F64 = 28, BF16 = 30
    }

    public static class GgmlTypes
    {
        // Elements per block and bytes per block (sizeof(block_*) in ggml-common.h).
        public static bool TryGetLayout(GgmlType t, out int blockSize, out int typeSize)
        {
            switch (t)
            {
                case GgmlType.F32:  blockSize = 1;   typeSize = 4;   return true;
                case GgmlType.F16:  blockSize = 1;   typeSize = 2;   return true;
                case GgmlType.BF16: blockSize = 1;   typeSize = 2;   return true;
                case GgmlType.I8:   blockSize = 1;   typeSize = 1;   return true;
                case GgmlType.I16:  blockSize = 1;   typeSize = 2;   return true;
                case GgmlType.I32:  blockSize = 1;   typeSize = 4;   return true;
                case GgmlType.I64:  blockSize = 1;   typeSize = 8;   return true;
                case GgmlType.F64:  blockSize = 1;   typeSize = 8;   return true;
                case GgmlType.Q4_0: blockSize = 32;  typeSize = 18;  return true;
                case GgmlType.Q4_1: blockSize = 32;  typeSize = 20;  return true;
                case GgmlType.Q5_0: blockSize = 32;  typeSize = 22;  return true;
                case GgmlType.Q5_1: blockSize = 32;  typeSize = 24;  return true;
                case GgmlType.Q8_0: blockSize = 32;  typeSize = 34;  return true;
                case GgmlType.Q8_1: blockSize = 32;  typeSize = 36;  return true;
                case GgmlType.Q2_K: blockSize = 256; typeSize = 84;  return true;
                case GgmlType.Q3_K: blockSize = 256; typeSize = 110; return true;
                case GgmlType.Q4_K: blockSize = 256; typeSize = 144; return true;
                case GgmlType.Q5_K: blockSize = 256; typeSize = 176; return true;
                case GgmlType.Q6_K: blockSize = 256; typeSize = 210; return true;
                case GgmlType.Q8_K: blockSize = 256; typeSize = 292; return true;
            }
            blockSize = 0; typeSize = 0;
            return false;
        }

        public static string Name(GgmlType t)
        {
            if (Enum.IsDefined(typeof(GgmlType), t)) return t.ToString().ToLowerInvariant().Replace("_k", "_K");
            return "type" + (int)t;
        }
    }

    public sealed class GgufValue
    {
        public GgufType Type;
        public GgufType ArrayType;   // valid when Type == Array
        public object Value;         // scalar (boxed), string, or typed array (string[], int[], float[], ...)
    }

    public sealed class GgufTensor
    {
        public string Name;
        public long[] Dims;          // Dims[0] is the contiguous (row) dimension
        public GgmlType Type;
        public int FileIndex;        // which split file holds it
        public long FileOffset;      // absolute offset in that file
        public long SizeBytes;

        public long ElementCount
        {
            get { long n = 1; for (int i = 0; i < Dims.Length; i++) n *= Dims[i]; return n; }
        }
        public int Cols { get { return (int)Dims[0]; } }
        public int Rows { get { return (int)(ElementCount / Dims[0]); } }
    }

    public sealed class GgufModelFile
    {
        public const uint Magic = 0x46554747; // "GGUF" little-endian

        public readonly List<string> Paths = new List<string>();
        public readonly Dictionary<string, GgufValue> Metadata = new Dictionary<string, GgufValue>();
        public readonly List<GgufTensor> Tensors = new List<GgufTensor>();
        public readonly Dictionary<string, GgufTensor> TensorsByName = new Dictionary<string, GgufTensor>();

        static readonly Regex SplitName = new Regex(@"^(.*)-(\d{5})-of-(\d{5})\.gguf$", RegexOptions.IgnoreCase);

        public long TotalBytes
        {
            get { long n = 0; foreach (GgufTensor t in Tensors) n += t.SizeBytes; return n; }
        }

        // Opens a .gguf file, or every part of a split model when given any one part.
        public static GgufModelFile Open(string path)
        {
            path = Path.GetFullPath(path);
            List<string> parts = new List<string>();
            Match m = SplitName.Match(path);
            if (m.Success)
            {
                int count = int.Parse(m.Groups[3].Value);
                for (int i = 1; i <= count; i++)
                {
                    string p = string.Format("{0}-{1:D5}-of-{2:D5}.gguf", m.Groups[1].Value, i, count);
                    if (!File.Exists(p)) throw new FileNotFoundException("Split model part is missing: " + p, p);
                    parts.Add(p);
                }
            }
            else parts.Add(path);

            GgufModelFile g = new GgufModelFile();
            for (int i = 0; i < parts.Count; i++) g.ReadHeader(parts[i], i);
            return g;
        }

        void ReadHeader(string path, int fileIndex)
        {
            Paths.Add(path);
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
            {
                long fileLength = fs.Length;
                BinaryReader r = new BinaryReader(fs, Encoding.UTF8);
                if (fileLength < 24 || r.ReadUInt32() != Magic) throw new InvalidDataException(path + " is not a GGUF file.");
                uint version = r.ReadUInt32();
                if (version < 2 || version > 3) throw new InvalidDataException(path + ": unsupported GGUF version " + version + ".");
                long nTensors = r.ReadInt64(), nKv = r.ReadInt64();
                if (nTensors < 0 || nKv < 0 || nTensors > 1000000 || nKv > 1000000) throw new InvalidDataException(path + ": bad tensor or key count.");

                int alignment = 32;
                for (long i = 0; i < nKv; i++)
                {
                    string key = ReadString(r);
                    GgufValue v = new GgufValue();
                    v.Type = (GgufType)r.ReadInt32();
                    if (v.Type == GgufType.Array)
                    {
                        v.ArrayType = (GgufType)r.ReadInt32();
                        v.Value = ReadArray(r, v.ArrayType, r.ReadInt64());
                    }
                    else v.Value = ReadScalar(r, v.Type);
                    if (key == "general.alignment") alignment = (int)Convert.ToUInt32(v.Value);
                    // Split parts repeat a few keys (split.no differs); the first part wins.
                    if (!Metadata.ContainsKey(key)) Metadata.Add(key, v);
                }
                if (alignment <= 0 || (alignment & (alignment - 1)) != 0) throw new InvalidDataException(path + ": general.alignment must be a power of two.");

                List<GgufTensor> mine = new List<GgufTensor>();
                for (long i = 0; i < nTensors; i++)
                {
                    GgufTensor t = new GgufTensor();
                    t.Name = ReadString(r);
                    uint nDims = r.ReadUInt32();
                    if (nDims == 0 || nDims > 4) throw new InvalidDataException("Tensor " + t.Name + " has " + nDims + " dims.");
                    t.Dims = new long[nDims];
                    for (int d = 0; d < nDims; d++)
                    {
                        t.Dims[d] = r.ReadInt64();
                        if (t.Dims[d] <= 0) throw new InvalidDataException("Tensor " + t.Name + " has a non-positive dim.");
                    }
                    t.Type = (GgmlType)r.ReadInt32();
                    t.FileOffset = (long)r.ReadUInt64();
                    t.FileIndex = fileIndex;
                    if (TensorsByName.ContainsKey(t.Name)) throw new InvalidDataException("Duplicate tensor " + t.Name + ".");
                    mine.Add(t);
                }

                long dataOffset = (fs.Position + alignment - 1) / alignment * alignment;
                foreach (GgufTensor t in mine)
                {
                    int blockSize, typeSize;
                    if (!GgmlTypes.TryGetLayout(t.Type, out blockSize, out typeSize))
                        throw new NotSupportedException("Tensor " + t.Name + " uses " + GgmlTypes.Name(t.Type) + ", which Windlass cannot read yet.");
                    if (t.Dims[0] % blockSize != 0) throw new InvalidDataException("Tensor " + t.Name + " row is not a multiple of the block size.");
                    t.SizeBytes = t.ElementCount / blockSize * typeSize;
                    t.FileOffset += dataOffset;
                    if (t.FileOffset + t.SizeBytes > fileLength) throw new InvalidDataException("Tensor " + t.Name + " extends past the end of " + path + ".");
                    Tensors.Add(t);
                    TensorsByName.Add(t.Name, t);
                }
            }
        }

        public bool Has(string key) { return Metadata.ContainsKey(key); }
        public string GetString(string key) { return (string)Metadata[key].Value; }
        public long GetInt(string key) { return Convert.ToInt64(Metadata[key].Value); }
        public float GetFloat(string key) { return Convert.ToSingle(Metadata[key].Value); }
        public T[] GetArray<T>(string key) { return (T[])Metadata[key].Value; }

        public string GetString(string key, string dflt) { return Has(key) ? GetString(key) : dflt; }
        public long GetInt(string key, long dflt) { return Has(key) ? GetInt(key) : dflt; }
        public float GetFloat(string key, float dflt) { return Has(key) ? GetFloat(key) : dflt; }

        static string ReadString(BinaryReader r)
        {
            ulong len = r.ReadUInt64();
            if (len > int.MaxValue) throw new InvalidDataException("GGUF string too long.");
            byte[] b = r.ReadBytes((int)len);
            if (b.Length != (int)len) throw new EndOfStreamException("Unexpected end of GGUF header.");
            return Encoding.UTF8.GetString(b);
        }

        static object ReadScalar(BinaryReader r, GgufType t)
        {
            switch (t)
            {
                case GgufType.UInt8:   return r.ReadByte();
                case GgufType.Int8:    return r.ReadSByte();
                case GgufType.UInt16:  return r.ReadUInt16();
                case GgufType.Int16:   return r.ReadInt16();
                case GgufType.UInt32:  return r.ReadUInt32();
                case GgufType.Int32:   return r.ReadInt32();
                case GgufType.Float32: return r.ReadSingle();
                case GgufType.Bool:    return r.ReadByte() != 0;
                case GgufType.String:  return ReadString(r);
                case GgufType.UInt64:  return r.ReadUInt64();
                case GgufType.Int64:   return r.ReadInt64();
                case GgufType.Float64: return r.ReadDouble();
            }
            throw new InvalidDataException("Unknown GGUF value type " + (int)t + ".");
        }

        static object ReadArray(BinaryReader r, GgufType t, long n)
        {
            if (n < 0 || n > int.MaxValue) throw new InvalidDataException("Bad GGUF array length.");
            int count = (int)n;
            switch (t)
            {
                case GgufType.String:  { string[] a = new string[count]; for (int i = 0; i < count; i++) a[i] = ReadString(r); return a; }
                case GgufType.UInt8:   { byte[] a = new byte[count];     for (int i = 0; i < count; i++) a[i] = r.ReadByte(); return a; }
                case GgufType.Int8:    { sbyte[] a = new sbyte[count];   for (int i = 0; i < count; i++) a[i] = r.ReadSByte(); return a; }
                case GgufType.Bool:    { bool[] a = new bool[count];     for (int i = 0; i < count; i++) a[i] = r.ReadByte() != 0; return a; }
                case GgufType.UInt16:  { ushort[] a = new ushort[count]; for (int i = 0; i < count; i++) a[i] = r.ReadUInt16(); return a; }
                case GgufType.Int16:   { short[] a = new short[count];   for (int i = 0; i < count; i++) a[i] = r.ReadInt16(); return a; }
                case GgufType.UInt32:  { uint[] a = new uint[count];     for (int i = 0; i < count; i++) a[i] = r.ReadUInt32(); return a; }
                case GgufType.Int32:   { int[] a = new int[count];       for (int i = 0; i < count; i++) a[i] = r.ReadInt32(); return a; }
                case GgufType.Float32: { float[] a = new float[count];   for (int i = 0; i < count; i++) a[i] = r.ReadSingle(); return a; }
                case GgufType.UInt64:  { ulong[] a = new ulong[count];   for (int i = 0; i < count; i++) a[i] = r.ReadUInt64(); return a; }
                case GgufType.Int64:   { long[] a = new long[count];     for (int i = 0; i < count; i++) a[i] = r.ReadInt64(); return a; }
                case GgufType.Float64: { double[] a = new double[count]; for (int i = 0; i < count; i++) a[i] = r.ReadDouble(); return a; }
            }
            throw new InvalidDataException("Unsupported GGUF array type " + (int)t + ".");
        }
    }
}

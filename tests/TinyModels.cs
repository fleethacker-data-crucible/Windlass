// Test fixtures: a GGUF writer and tiny random models (Llama, Qwen2, Qwen3; byte-level BPE or SentencePiece
// vocabularies; any supported weight type). C# 5 only. Deterministic: the same spec always gives the same file,
// so tests/fixtures/llama-cpp-reference.txt (made with llama.cpp) stays valid.
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace Windlass.Tests
{
    public sealed class GgufWriter
    {
        sealed class Tensor { public string Name; public long[] Dims; public GgmlType Type; public byte[] Data; }

        readonly List<KeyValuePair<string, object>> _kv = new List<KeyValuePair<string, object>>();
        readonly List<Tensor> _tensors = new List<Tensor>();

        public void Add(string key, object value) { _kv.Add(new KeyValuePair<string, object>(key, value)); }

        public void AddTensor(string name, GgmlType type, byte[] data, params long[] dims)
        {
            _tensors.Add(new Tensor { Name = name, Type = type, Data = data, Dims = dims });
        }

        public void Save(string path) { Save(path, _kv, _tensors); }

        // Writes the tensors in two files, as gguf-split does: all metadata in part 1, split.* keys in both.
        public void SaveSplit(string path1, string path2, int firstPartTensors)
        {
            List<KeyValuePair<string, object>> kv1 = new List<KeyValuePair<string, object>>(_kv);
            kv1.Add(new KeyValuePair<string, object>("split.no", 0u));
            kv1.Add(new KeyValuePair<string, object>("split.count", 2u));
            List<KeyValuePair<string, object>> kv2 = new List<KeyValuePair<string, object>>();
            kv2.Add(new KeyValuePair<string, object>("split.no", 1u));
            kv2.Add(new KeyValuePair<string, object>("split.count", 2u));
            Save(path1, kv1, _tensors.GetRange(0, firstPartTensors));
            Save(path2, kv2, _tensors.GetRange(firstPartTensors, _tensors.Count - firstPartTensors));
        }

        static void Save(string path, List<KeyValuePair<string, object>> kvs, List<Tensor> tensors)
        {
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write))
            {
                BinaryWriter w = new BinaryWriter(fs, new UTF8Encoding(false));
                w.Write(GgufModelFile.Magic);
                w.Write(3u);
                w.Write((long)tensors.Count);
                w.Write((long)kvs.Count);
                foreach (KeyValuePair<string, object> kv in kvs) { Str(w, kv.Key); Value(w, kv.Value); }
                long off = 0;
                foreach (Tensor t in tensors)
                {
                    Str(w, t.Name);
                    w.Write((uint)t.Dims.Length);
                    foreach (long d in t.Dims) w.Write(d);
                    w.Write((int)t.Type);
                    w.Write((ulong)off);
                    off += (t.Data.Length + 31) / 32 * 32;
                }
                Pad(w);
                foreach (Tensor t in tensors) { w.Write(t.Data); Pad(w); }
            }
        }

        static void Pad(BinaryWriter w) { while (w.BaseStream.Position % 32 != 0) w.Write((byte)0); }

        static void Str(BinaryWriter w, string s)
        {
            byte[] b = Encoding.UTF8.GetBytes(s);
            w.Write((ulong)b.Length);
            w.Write(b);
        }

        static void Value(BinaryWriter w, object v)
        {
            if (v is string) { w.Write((int)GgufType.String); Str(w, (string)v); }
            else if (v is uint) { w.Write((int)GgufType.UInt32); w.Write((uint)v); }
            else if (v is int) { w.Write((int)GgufType.Int32); w.Write((int)v); }
            else if (v is float) { w.Write((int)GgufType.Float32); w.Write((float)v); }
            else if (v is bool) { w.Write((int)GgufType.Bool); w.Write((byte)((bool)v ? 1 : 0)); }
            else if (v is string[]) { w.Write((int)GgufType.Array); w.Write((int)GgufType.String); string[] a = (string[])v; w.Write((long)a.Length); foreach (string s in a) Str(w, s); }
            else if (v is int[]) { w.Write((int)GgufType.Array); w.Write((int)GgufType.Int32); int[] a = (int[])v; w.Write((long)a.Length); foreach (int x in a) w.Write(x); }
            else if (v is float[]) { w.Write((int)GgufType.Array); w.Write((int)GgufType.Float32); float[] a = (float[])v; w.Write((long)a.Length); foreach (float x in a) w.Write(x); }
            else throw new ArgumentException("Unsupported GGUF value " + v.GetType());
        }
    }

    // xorshift64*: identical on every runtime (System.Random is not guaranteed to be).
    public sealed class Rng
    {
        ulong _s;
        public Rng(ulong seed) { _s = seed * 0x9E3779B97F4A7C15UL + 1; }
        public ulong Next() { _s ^= _s >> 12; _s ^= _s << 25; _s ^= _s >> 27; return _s * 2685821657736338717UL; }
        public byte Byte() { return (byte)(Next() >> 56); }
        public double Uniform() { return (Next() >> 11) * (1.0 / 9007199254740992.0); }
        public float Normal() { double u = Math.Max(Uniform(), 1e-12), v = Uniform(); return (float)(Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v)); }
    }

    public sealed class TinySpec
    {
        public string Arch = "llama";       // llama, qwen2, qwen3
        public GgmlType Type = GgmlType.Q8_0;
        public bool Tied;
        public bool Spm;                    // SentencePiece vocabulary (else byte-level BPE)
        public bool RopeFreqs;              // Llama 3.1-style rope_freqs.weight
        public int Layers = 2, Embd = 256, Heads = 4, KvHeads = 2, Ff = 512;
        public ulong Seed = 1;

        public string Key
        {
            get { return Arch + "-" + GgmlTypes.Name(Type) + (Tied ? "-tied" : "") + (Spm ? "-spm" : "") + (RopeFreqs ? "-ropefreqs" : ""); }
        }
    }

    public static unsafe class TinyModels
    {
        public static readonly string[] BpeMerges =
        {
            "\u0120 t", "h e", "\u0120t he", "i n", "\u0120 a", "e r", "o n", "\u0120 w", "r e", "\u0120 s", "a t", "o r", "e n", "\u0120 o", "\u0120 c",
            "i s", "a n", "\u0120 b", "l l", "\u0120 h", "e s", "i t", "\u0120 l", "\u0120 d", "\u0120 in", "\u0120 f", "a r", "\u0120 m", "\u0120 p", "o u",
            "\u0120w or", "l d", "\u0120wor ld", "\u0120 is", "H e", "He ll", "Hell o", "\u0120 1", "\u0120 2", "\u010A \u010A"
        };

        // Byte-level BPE vocabulary: 256 byte tokens, the merges above, then the specials of the chat template.
        public static void BpeVocab(TinySpec s, out string[] tokens, out int[] types, out string[] merges, out int bos, out int eos)
        {
            List<string> t = new List<string>();
            List<int> ty = new List<int>();
            for (int b = 0; b < 256; b++) { t.Add(ByteChar(b)); ty.Add(1); }
            foreach (string m in BpeMerges) { t.Add(m.Replace(" ", "")); ty.Add(1); }
            string[] specials = s.Arch == "llama"
                ? new string[] { "<|begin_of_text|>", "<|end_of_text|>", "<|start_header_id|>", "<|end_header_id|>", "<|eot_id|>" }
                : new string[] { "<|endoftext|>", "<|im_start|>", "<|im_end|>" };
            foreach (string sp in specials) { t.Add(sp); ty.Add(3); }
            tokens = t.ToArray(); types = ty.ToArray(); merges = BpeMerges;
            bos = s.Arch == "llama" ? t.IndexOf("<|begin_of_text|>") : t.IndexOf("<|endoftext|>");
            eos = s.Arch == "llama" ? t.IndexOf("<|eot_id|>") : t.IndexOf("<|im_end|>");
        }

        static string ByteChar(int b)
        {
            int n = 0;
            for (int i = 0; i < 256; i++)
            {
                bool printable = (i >= 0x21 && i <= 0x7E) || (i >= 0xA1 && i <= 0xAC) || (i >= 0xAE && i <= 0xFF);
                char c = printable ? (char)i : (char)(256 + n++);
                if (i == b) return c.ToString();
            }
            throw new ArgumentException();
        }

        public static readonly string[] SpmPieces =
        {
            "\u2581", "\u2581t", "he", "\u2581the", "\u2581a", "er", "on", "\u2581w", "re", "\u2581s", "in", "\u2581the\u2581",
            "\u2581world", "\u2581is", "\u2581Hello", "ll", "\u2581H", "\u2581[", "INST", "]", "[/", "\u2581[/INST]", "\u2581[INST]"
        };

        // SentencePiece vocabulary: <unk> <s> </s>, 256 byte tokens, letters, then pieces scored by length.
        public static void SpmVocab(out string[] tokens, out int[] types, out float[] scores)
        {
            List<string> t = new List<string> { "<unk>", "<s>", "</s>" };
            List<int> ty = new List<int> { 2, 3, 3 };
            List<float> sc = new List<float> { 0, 0, 0 };
            for (int b = 0; b < 256; b++) { t.Add("<0x" + b.ToString("X2") + ">"); ty.Add(6); sc.Add(0); }
            for (char c = 'a'; c <= 'z'; c++) { t.Add(c.ToString()); ty.Add(1); sc.Add(-100 - (c - 'a')); }
            for (char c = 'A'; c <= 'Z'; c++) { t.Add(c.ToString()); ty.Add(1); sc.Add(-200 - (c - 'A')); }
            for (int i = 0; i < SpmPieces.Length; i++) { t.Add(SpmPieces[i]); ty.Add(1); sc.Add(-i * 1.5f); }
            tokens = t.ToArray(); types = ty.ToArray(); scores = sc.ToArray();
        }

        // The models the reference fixture covers: every weight type on Llama, plus each architecture and option.
        public static List<TinySpec> StandardSpecs()
        {
            List<TinySpec> specs = new List<TinySpec>();
            foreach (GgmlType t in new GgmlType[] { GgmlType.F32, GgmlType.F16, GgmlType.BF16, GgmlType.Q4_0, GgmlType.Q4_1, GgmlType.Q5_0,
                GgmlType.Q5_1, GgmlType.Q8_0, GgmlType.Q2_K, GgmlType.Q3_K, GgmlType.Q4_K, GgmlType.Q5_K, GgmlType.Q6_K })
                specs.Add(new TinySpec { Arch = "llama", Type = t });
            specs.Add(new TinySpec { Arch = "qwen2", Type = GgmlType.Q8_0 });
            specs.Add(new TinySpec { Arch = "qwen3", Type = GgmlType.Q4_K });
            specs.Add(new TinySpec { Arch = "qwen2", Type = GgmlType.Q6_K, Tied = true });
            specs.Add(new TinySpec { Arch = "llama", Type = GgmlType.Q8_0, Spm = true });
            specs.Add(new TinySpec { Arch = "llama", Type = GgmlType.F16, RopeFreqs = true });
            return specs;
        }

        // The token ids the reference logits were computed for (valid in every tiny vocabulary).
        public static readonly int[] ProbeTokens = { 1, 10, 50, 100, 200, 257, 290, 5, 7, 99, 33, 72 };

        public static string Write(string path, TinySpec s)
        {
            Build(s).Save(path);
            return path;
        }

        // Two-part split model (name-00001-of-00002.gguf): the first `firstPartTensors` tensors in part 1.
        public static string WriteSplit(string dir, TinySpec s, int firstPartTensors)
        {
            string p1 = Path.Combine(dir, "split-" + s.Key + "-00001-of-00002.gguf");
            string p2 = Path.Combine(dir, "split-" + s.Key + "-00002-of-00002.gguf");
            Build(s).SaveSplit(p1, p2, firstPartTensors);
            return p1;
        }

        public static GgufWriter Build(TinySpec s)
        {
            Rng rng = new Rng(s.Seed);
            GgufWriter w = new GgufWriter();
            string a = s.Arch + ".";
            int headDim = s.Embd / s.Heads;
            w.Add("general.architecture", s.Arch);
            w.Add("general.name", "tiny-" + s.Key);
            w.Add("general.alignment", 32u);
            w.Add(a + "block_count", (uint)s.Layers);
            w.Add(a + "context_length", 512u);
            w.Add(a + "embedding_length", (uint)s.Embd);
            w.Add(a + "feed_forward_length", (uint)s.Ff);
            w.Add(a + "attention.head_count", (uint)s.Heads);
            w.Add(a + "attention.head_count_kv", (uint)s.KvHeads);
            w.Add(a + "attention.layer_norm_rms_epsilon", 1e-5f);
            w.Add(a + "rope.freq_base", s.Arch == "llama" ? 500000f : 1000000f);
            if (s.Arch == "qwen3") w.Add(a + "attention.key_length", (uint)headDim);
            if (s.Arch == "qwen3") w.Add(a + "attention.value_length", (uint)headDim);
            if (s.Arch == "llama") w.Add(a + "rope.dimension_count", (uint)headDim);

            int vocab;
            if (s.Spm)
            {
                string[] tokens; int[] types; float[] scores;
                SpmVocab(out tokens, out types, out scores);
                w.Add("tokenizer.ggml.model", "llama");
                w.Add("tokenizer.ggml.tokens", tokens);
                w.Add("tokenizer.ggml.scores", scores);
                w.Add("tokenizer.ggml.token_type", types);
                w.Add("tokenizer.ggml.bos_token_id", 1u);
                w.Add("tokenizer.ggml.eos_token_id", 2u);
                w.Add("tokenizer.ggml.unknown_token_id", 0u);
                w.Add("tokenizer.chat_template", "{{ bos_token }}{% for message in messages %}[INST] {{ message['content'] }} [/INST]{% endfor %}");
                vocab = tokens.Length;
            }
            else
            {
                string[] tokens, merges; int[] types; int bos, eos;
                BpeVocab(s, out tokens, out types, out merges, out bos, out eos);
                w.Add("tokenizer.ggml.model", "gpt2");
                w.Add("tokenizer.ggml.pre", s.Arch == "llama" ? "llama-bpe" : "qwen2");
                w.Add("tokenizer.ggml.tokens", tokens);
                w.Add("tokenizer.ggml.token_type", types);
                w.Add("tokenizer.ggml.merges", merges);
                w.Add("tokenizer.ggml.bos_token_id", (uint)bos);
                w.Add("tokenizer.ggml.eos_token_id", (uint)eos);
                w.Add("tokenizer.ggml.add_bos_token", s.Arch == "llama");
                w.Add("tokenizer.chat_template", s.Arch == "llama" ? "{{ '<|start_header_id|>' }}" : "{{ '<|im_start|>' }}");
                vocab = tokens.Length;
            }

            int qd = s.Heads * headDim, kd = s.KvHeads * headDim;
            w.AddTensor("token_embd.weight", s.Type, Matrix(rng, s.Type, vocab, s.Embd, 1.0f), s.Embd, vocab);
            if (s.RopeFreqs)
            {
                float[] f = new float[headDim / 2];
                for (int i = 0; i < f.Length; i++) f[i] = 1f + i * 0.25f;
                w.AddTensor("rope_freqs.weight", GgmlType.F32, F32(f), f.Length);
            }
            for (int l = 0; l < s.Layers; l++)
            {
                string p = "blk." + l + ".";
                w.AddTensor(p + "attn_norm.weight", GgmlType.F32, Norm(rng, s.Embd), s.Embd);
                w.AddTensor(p + "attn_q.weight", s.Type, Matrix(rng, s.Type, qd, s.Embd, 0.08f), s.Embd, qd);
                w.AddTensor(p + "attn_k.weight", s.Type, Matrix(rng, s.Type, kd, s.Embd, 0.08f), s.Embd, kd);
                w.AddTensor(p + "attn_v.weight", s.Type, Matrix(rng, s.Type, kd, s.Embd, 0.08f), s.Embd, kd);
                if (s.Arch == "qwen2")
                {
                    w.AddTensor(p + "attn_q.bias", GgmlType.F32, Vec(rng, qd, 0.1f), qd);
                    w.AddTensor(p + "attn_k.bias", GgmlType.F32, Vec(rng, kd, 0.1f), kd);
                    w.AddTensor(p + "attn_v.bias", GgmlType.F32, Vec(rng, kd, 0.1f), kd);
                }
                if (s.Arch == "qwen3")
                {
                    w.AddTensor(p + "attn_q_norm.weight", GgmlType.F32, Norm(rng, headDim), headDim);
                    w.AddTensor(p + "attn_k_norm.weight", GgmlType.F32, Norm(rng, headDim), headDim);
                }
                w.AddTensor(p + "attn_output.weight", s.Type, Matrix(rng, s.Type, s.Embd, qd, 0.08f), qd, s.Embd);
                w.AddTensor(p + "ffn_norm.weight", GgmlType.F32, Norm(rng, s.Embd), s.Embd);
                w.AddTensor(p + "ffn_gate.weight", s.Type, Matrix(rng, s.Type, s.Ff, s.Embd, 0.08f), s.Embd, s.Ff);
                w.AddTensor(p + "ffn_up.weight", s.Type, Matrix(rng, s.Type, s.Ff, s.Embd, 0.08f), s.Embd, s.Ff);
                w.AddTensor(p + "ffn_down.weight", s.Type, Matrix(rng, s.Type, s.Embd, s.Ff, 0.06f), s.Ff, s.Embd);
            }
            w.AddTensor("output_norm.weight", GgmlType.F32, Norm(rng, s.Embd), s.Embd);
            if (!s.Tied) w.AddTensor("output.weight", s.Type, Matrix(rng, s.Type, vocab, s.Embd, 0.08f), s.Embd, vocab);
            return w;
        }

        static byte[] F32(float[] v)
        {
            byte[] b = new byte[v.Length * 4];
            Buffer.BlockCopy(v, 0, b, 0, b.Length);
            return b;
        }

        static byte[] Norm(Rng rng, int n)
        {
            float[] v = new float[n];
            for (int i = 0; i < n; i++) v[i] = 1f + 0.2f * rng.Normal();
            return F32(v);
        }

        static byte[] Vec(Rng rng, int n, float scale)
        {
            float[] v = new float[n];
            for (int i = 0; i < n; i++) v[i] = scale * rng.Normal();
            return F32(v);
        }

        // Offsets of the scale fields (d, then dmin/m when present) in one block of each quantized type.
        static int[] ScaleFields(GgmlType t)
        {
            switch (t)
            {
                case GgmlType.Q4_0: case GgmlType.Q5_0: case GgmlType.Q8_0: return new int[] { 0 };
                case GgmlType.Q4_1: case GgmlType.Q5_1: case GgmlType.Q4_K: case GgmlType.Q5_K: return new int[] { 0, 2 };
                case GgmlType.Q2_K: return new int[] { 80, 82 };
                case GgmlType.Q3_K: return new int[] { 108 };
                case GgmlType.Q6_K: return new int[] { 208 };
            }
            return null;
        }

        // A random rows x cols matrix with element RMS about `rms`. Float types are drawn directly; quantized types get
        // random block bytes whose scale fields are then set so the decoded values have that RMS.
        public static byte[] Matrix(Rng rng, GgmlType t, int rows, int cols, float rms)
        {
            int blockSize, typeSize;
            GgmlTypes.TryGetLayout(t, out blockSize, out typeSize);
            long n = (long)rows * cols;
            byte[] data = new byte[n / blockSize * typeSize];
            if (t == GgmlType.F32 || t == GgmlType.F16 || t == GgmlType.BF16)
            {
                for (long i = 0; i < n; i++)
                {
                    float v = rms * rng.Normal();
                    if (t == GgmlType.F32) Buffer.BlockCopy(new float[] { v }, 0, data, (int)(i * 4), 4);
                    else
                    {
                        ushort h = t == GgmlType.F16 ? Half.FromFloat(v) : (ushort)(BitConverter.ToUInt32(BitConverter.GetBytes(v), 0) >> 16);
                        data[i * 2] = (byte)h; data[i * 2 + 1] = (byte)(h >> 8);
                    }
                }
                return data;
            }
            for (int i = 0; i < data.Length; i++) data[i] = rng.Byte();
            int[] fields = ScaleFields(t);
            int blocks = data.Length / typeSize;
            float[] tmp = new float[blockSize];
            for (int b = 0; b < blocks; b++)
            {
                int o = b * typeSize;
                foreach (int f in fields) { ushort one = Half.FromFloat(1f); data[o + f] = (byte)one; data[o + f + 1] = (byte)(one >> 8); }
                double sum = 0;
                fixed (byte* p = data) fixed (float* d = tmp) Quants.DecodeRow(t, p + o, blockSize, d);
                foreach (float v in tmp) sum += v * v;
                float scale = (float)(rms / Math.Sqrt(Math.Max(sum / blockSize, 1e-12)));
                // Spread the scales a little so blocks differ.
                scale *= (float)(0.75 + 0.5 * rng.Uniform());
                foreach (int f in fields) { ushort h = Half.FromFloat(scale); data[o + f] = (byte)h; data[o + f + 1] = (byte)(h >> 8); }
            }
            return data;
        }
    }
}

// Test fixtures: tiny random Granite and OLMo 2 models (and the Llama twin of a Granite model: the same tensors, so a
// Granite file with no scale keys must give exactly the Llama logits). C# 5 only. Deterministic per spec.
using System;
using System.Collections.Generic;

namespace Windlass.Tests
{
    public sealed class ArchSpec
    {
        public string Arch = "granite";     // granite, olmo2, llama (the twin)
        public GgmlType Type = GgmlType.Q8_0;
        public bool Tied;
        public int Layers = 2, Embd = 256, Heads = 4, KvHeads = 2, Ff = 512;
        public float EmbeddingScale, ResidualScale, AttentionScale, LogitScale;   // 0 = leave the key out (granite only)
        public string Omit;                 // leave this per-layer tensor out (for example post_ffw_norm), to test refusals
        public bool ShortQNorm;             // olmo2: attn_q_norm covers one head, not the whole vector (must be refused)
        public ulong Seed = 1;

        public int HeadDim { get { return Embd / Heads; } }

        public string Key
        {
            get
            {
                return "arch-" + Arch + "-" + GgmlTypes.Name(Type) + (Tied ? "-tied" : "")
                    + (EmbeddingScale != 0 || ResidualScale != 0 || AttentionScale != 0 || LogitScale != 0 ? "-scaled" + EmbeddingScale + "_" + ResidualScale + "_" + AttentionScale + "_" + LogitScale : "")
                    + (Omit != null ? "-no" + Omit : "") + (ShortQNorm ? "-shortq" : "") + "-s" + Seed;
            }
        }
    }

    public static class TinyArchs
    {
        public static string Write(string path, ArchSpec s) { Build(s).Save(path); return path; }

        public static GgufWriter Build(ArchSpec s)
        {
            Rng rng = new Rng(s.Seed);
            GgufWriter w = new GgufWriter();
            string a = s.Arch + ".";
            int hd = s.HeadDim, E = s.Embd, qd = s.Heads * hd, kd = s.KvHeads * hd, F = s.Ff;
            bool olmo = s.Arch == "olmo2";
            w.Add("general.architecture", s.Arch);
            w.Add("general.name", "tiny-" + s.Key);
            w.Add("general.alignment", 32u);
            w.Add(a + "block_count", (uint)s.Layers);
            w.Add(a + "context_length", 512u);
            w.Add(a + "embedding_length", (uint)E);
            w.Add(a + "feed_forward_length", (uint)F);
            w.Add(a + "attention.head_count", (uint)s.Heads);
            w.Add(a + "attention.head_count_kv", (uint)s.KvHeads);
            w.Add(a + "attention.layer_norm_rms_epsilon", 1e-5f);
            w.Add(a + "rope.freq_base", 500000f);
            if (!olmo) w.Add(a + "rope.dimension_count", (uint)hd);
            if (s.Arch == "granite")
            {
                if (s.EmbeddingScale != 0) w.Add(a + "embedding_scale", s.EmbeddingScale);
                if (s.ResidualScale != 0) w.Add(a + "residual_scale", s.ResidualScale);
                if (s.AttentionScale != 0) w.Add(a + "attention.scale", s.AttentionScale);
                if (s.LogitScale != 0) w.Add(a + "logit_scale", s.LogitScale);
            }

            TinySpec vs = new TinySpec { Arch = "llama" };
            string[] tokens, merges; int[] types; int bos, eos;
            TinyModels.BpeVocab(vs, out tokens, out types, out merges, out bos, out eos);
            w.Add("tokenizer.ggml.model", "gpt2");
            w.Add("tokenizer.ggml.pre", "llama-bpe");
            w.Add("tokenizer.ggml.tokens", tokens);
            w.Add("tokenizer.ggml.token_type", types);
            w.Add("tokenizer.ggml.merges", merges);
            w.Add("tokenizer.ggml.bos_token_id", (uint)bos);
            w.Add("tokenizer.ggml.eos_token_id", (uint)eos);
            w.Add("tokenizer.ggml.add_bos_token", true);
            w.Add("tokenizer.chat_template", "{{ '<|start_header_id|>' }}");
            int vocab = tokens.Length;

            GgmlType ty = s.Type;
            w.AddTensor("token_embd.weight", ty, TinyModels.Matrix(rng, ty, vocab, E, 1.0f), E, vocab);
            for (int l = 0; l < s.Layers; l++)
            {
                string p = "blk." + l + ".";
                if (!olmo) w.AddTensor(p + "attn_norm.weight", GgmlType.F32, Norm(rng, E), E);
                w.AddTensor(p + "attn_q.weight", ty, TinyModels.Matrix(rng, ty, qd, E, 0.08f), E, qd);
                w.AddTensor(p + "attn_k.weight", ty, TinyModels.Matrix(rng, ty, kd, E, 0.08f), E, kd);
                w.AddTensor(p + "attn_v.weight", ty, TinyModels.Matrix(rng, ty, kd, E, 0.08f), E, kd);
                if (olmo)
                {
                    int qn = s.ShortQNorm ? hd : qd;
                    w.AddTensor(p + "attn_q_norm.weight", GgmlType.F32, Norm(rng, qn), qn);
                    w.AddTensor(p + "attn_k_norm.weight", GgmlType.F32, Norm(rng, kd), kd);
                }
                w.AddTensor(p + "attn_output.weight", ty, TinyModels.Matrix(rng, ty, E, qd, 0.08f), qd, E);
                if (olmo) w.AddTensor(p + "post_attention_norm.weight", GgmlType.F32, Norm(rng, E), E);
                else w.AddTensor(p + "ffn_norm.weight", GgmlType.F32, Norm(rng, E), E);
                w.AddTensor(p + "ffn_gate.weight", ty, TinyModels.Matrix(rng, ty, F, E, 0.08f), E, F);
                w.AddTensor(p + "ffn_up.weight", ty, TinyModels.Matrix(rng, ty, F, E, 0.08f), E, F);
                w.AddTensor(p + "ffn_down.weight", ty, TinyModels.Matrix(rng, ty, E, F, 0.06f), F, E);
                if (olmo && s.Omit != "post_ffw_norm") w.AddTensor(p + "post_ffw_norm.weight", GgmlType.F32, Norm(rng, E), E);
            }
            w.AddTensor("output_norm.weight", GgmlType.F32, Norm(rng, E), E);
            if (!s.Tied) w.AddTensor("output.weight", ty, TinyModels.Matrix(rng, ty, vocab, E, 0.08f), E, vocab);
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
    }
}

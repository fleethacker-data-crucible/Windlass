// Test fixtures: tiny random mixture-of-experts GGUF models in the three layouts llama.cpp uses (stacked expert
// tensors [cols, rows, experts]): Mixtral-style (arch llama or mixtral), qwen2moe (biases + shared expert) and qwen3moe
// (QK norm, some layers dense). Also the dense twin of a one-expert model. C# 5 only. Deterministic per spec.
using System;
using System.Collections.Generic;
using System.IO;

namespace Windlass.Tests
{
    public sealed class MoeSpec
    {
        public string Layout = "llama";     // llama, mixtral, qwen2moe, qwen3moe
        public GgmlType Type = GgmlType.Q8_0;
        public bool Tied;
        public int Layers = 2, Embd = 256, Heads = 4, KvHeads = 2, Experts = 4, Used = 2, ExpertFf = 256, SharedFf = 256, DenseFf = 256;
        public int NormKey = -1;            // expert_weights_norm in the GGUF: -1 absent, 0 false, 1 true
        public int[] DenseLayers = new int[0];   // layers with a plain FFN (Qwen3-MoE mlp_only_layers)
        public string Omit;                 // leave this tensor (for example ffn_up_exps) out of every MoE layer, to test refusals
        public int UsedKey = -1;            // expert_used_count to write (-1 = Used)
        public ulong Seed = 1;

        public bool Qwen { get { return Layout == "qwen2moe" || Layout == "qwen3moe"; } }
        public bool IsDense(int layer) { return Array.IndexOf(DenseLayers, layer) >= 0; }
        public int HeadDim { get { return Embd / Heads; } }

        public string Key
        {
            get
            {
                string dense = "";
                foreach (int d in DenseLayers) dense += d;
                return "moe-" + Layout + "-" + GgmlTypes.Name(Type) + "-e" + Experts + "k" + Used + (Tied ? "-tied" : "")
                    + (NormKey >= 0 ? "-norm" + NormKey : "") + (dense.Length > 0 ? "-dense" + dense : "")
                    + (Omit != null ? "-no" + Omit : "") + (UsedKey >= 0 ? "-used" + UsedKey : "") + "-s" + Seed;
            }
        }
    }

    public static class TinyMoe
    {
        public static string Write(string path, MoeSpec s) { Build(s, false).Save(path); return path; }

        // The same model with ordinary 2-D ffn_gate/up/down tensors holding the single expert (needs Experts == 1).
        public static string WriteDense(string path, MoeSpec s)
        {
            if (s.Experts != 1) throw new ArgumentException("A dense twin needs exactly one expert.");
            Build(s, true).Save(path);
            return path;
        }

        public static GgufWriter Build(MoeSpec s, bool dense)
        {
            Rng rng = new Rng(s.Seed);
            GgufWriter w = new GgufWriter();
            string arch = dense ? (s.Layout == "qwen3moe" ? "qwen3" : s.Layout == "qwen2moe" ? "qwen2" : "llama") : s.Layout;
            string a = arch + ".";
            int hd = s.HeadDim;
            bool qwen = s.Qwen;
            w.Add("general.architecture", arch);
            w.Add("general.name", "tiny-" + s.Key + (dense ? "-dense" : ""));
            w.Add("general.alignment", 32u);
            w.Add(a + "block_count", (uint)s.Layers);
            w.Add(a + "context_length", 512u);
            w.Add(a + "embedding_length", (uint)s.Embd);
            w.Add(a + "feed_forward_length", (uint)s.DenseFf);
            w.Add(a + "attention.head_count", (uint)s.Heads);
            w.Add(a + "attention.head_count_kv", (uint)s.KvHeads);
            w.Add(a + "attention.layer_norm_rms_epsilon", 1e-5f);
            w.Add(a + "rope.freq_base", qwen ? 1000000f : 500000f);
            if (arch == "qwen3" || arch == "qwen3moe") { w.Add(a + "attention.key_length", (uint)hd); w.Add(a + "attention.value_length", (uint)hd); }
            if (!qwen) w.Add(a + "rope.dimension_count", (uint)hd);
            if (!dense)
            {
                w.Add(a + "expert_count", (uint)s.Experts);
                w.Add(a + "expert_used_count", (uint)(s.UsedKey >= 0 ? s.UsedKey : s.Used));
                w.Add(a + "expert_feed_forward_length", (uint)s.ExpertFf);
                if (s.Layout == "qwen2moe") w.Add(a + "expert_shared_feed_forward_length", (uint)s.SharedFf);
                if (s.NormKey >= 0) w.Add(a + "expert_weights_norm", s.NormKey == 1);
            }

            TinySpec vs = new TinySpec { Arch = qwen ? "qwen2" : "llama" };
            string[] tokens, merges; int[] types; int bos, eos;
            TinyModels.BpeVocab(vs, out tokens, out types, out merges, out bos, out eos);
            w.Add("tokenizer.ggml.model", "gpt2");
            w.Add("tokenizer.ggml.pre", qwen ? "qwen2" : "llama-bpe");
            w.Add("tokenizer.ggml.tokens", tokens);
            w.Add("tokenizer.ggml.token_type", types);
            w.Add("tokenizer.ggml.merges", merges);
            w.Add("tokenizer.ggml.bos_token_id", (uint)bos);
            w.Add("tokenizer.ggml.eos_token_id", (uint)eos);
            w.Add("tokenizer.ggml.add_bos_token", !qwen);
            w.Add("tokenizer.chat_template", qwen ? "{{ '<|im_start|>' }}" : "{{ '<|start_header_id|>' }}");
            int vocab = tokens.Length;

            int E = s.Embd, qd = s.Heads * hd, kd = s.KvHeads * hd;
            GgmlType ty = s.Type;
            w.AddTensor("token_embd.weight", ty, TinyModels.Matrix(rng, ty, vocab, E, 1.0f), E, vocab);
            for (int l = 0; l < s.Layers; l++)
            {
                string p = "blk." + l + ".";
                w.AddTensor(p + "attn_norm.weight", GgmlType.F32, Norm(rng, E), E);
                w.AddTensor(p + "attn_q.weight", ty, TinyModels.Matrix(rng, ty, qd, E, 0.08f), E, qd);
                w.AddTensor(p + "attn_k.weight", ty, TinyModels.Matrix(rng, ty, kd, E, 0.08f), E, kd);
                w.AddTensor(p + "attn_v.weight", ty, TinyModels.Matrix(rng, ty, kd, E, 0.08f), E, kd);
                if (arch == "qwen2" || arch == "qwen2moe")
                {
                    w.AddTensor(p + "attn_q.bias", GgmlType.F32, Vec(rng, qd, 0.1f), qd);
                    w.AddTensor(p + "attn_k.bias", GgmlType.F32, Vec(rng, kd, 0.1f), kd);
                    w.AddTensor(p + "attn_v.bias", GgmlType.F32, Vec(rng, kd, 0.1f), kd);
                }
                if (arch == "qwen3" || arch == "qwen3moe")
                {
                    w.AddTensor(p + "attn_q_norm.weight", GgmlType.F32, Norm(rng, hd), hd);
                    w.AddTensor(p + "attn_k_norm.weight", GgmlType.F32, Norm(rng, hd), hd);
                }
                w.AddTensor(p + "attn_output.weight", ty, TinyModels.Matrix(rng, ty, E, qd, 0.08f), qd, E);
                w.AddTensor(p + "ffn_norm.weight", GgmlType.F32, Norm(rng, E), E);
                if (s.IsDense(l))
                {
                    int F = s.DenseFf;
                    w.AddTensor(p + "ffn_gate.weight", ty, TinyModels.Matrix(rng, ty, F, E, 0.08f), E, F);
                    w.AddTensor(p + "ffn_up.weight", ty, TinyModels.Matrix(rng, ty, F, E, 0.08f), E, F);
                    w.AddTensor(p + "ffn_down.weight", ty, TinyModels.Matrix(rng, ty, E, F, 0.06f), F, E);
                    continue;
                }
                int X = s.ExpertFf, N = s.Experts;
                // The router is drawn in the dense twin too, so every later weight is identical in both files.
                byte[] router = TinyModels.Matrix(rng, GgmlType.F32, N, E, 0.1f);
                byte[] gate = TinyModels.Matrix(rng, ty, N * X, E, 0.08f), up = TinyModels.Matrix(rng, ty, N * X, E, 0.08f), down = TinyModels.Matrix(rng, ty, N * E, X, 0.06f);
                if (dense)
                {
                    w.AddTensor(p + "ffn_gate.weight", ty, gate, E, X);
                    w.AddTensor(p + "ffn_up.weight", ty, up, E, X);
                    w.AddTensor(p + "ffn_down.weight", ty, down, X, E);
                    continue;
                }
                w.AddTensor(p + "ffn_gate_inp.weight", GgmlType.F32, router, E, N);
                if (s.Omit != "ffn_gate_exps") w.AddTensor(p + "ffn_gate_exps.weight", ty, gate, E, X, N);
                if (s.Omit != "ffn_up_exps") w.AddTensor(p + "ffn_up_exps.weight", ty, up, E, X, N);
                if (s.Omit != "ffn_down_exps") w.AddTensor(p + "ffn_down_exps.weight", ty, down, X, E, N);
                if (s.Layout == "qwen2moe")
                {
                    int H = s.SharedFf;
                    w.AddTensor(p + "ffn_gate_inp_shexp.weight", GgmlType.F32, Vec(rng, E, 0.2f), E);
                    w.AddTensor(p + "ffn_gate_shexp.weight", ty, TinyModels.Matrix(rng, ty, H, E, 0.08f), E, H);
                    w.AddTensor(p + "ffn_up_shexp.weight", ty, TinyModels.Matrix(rng, ty, H, E, 0.08f), E, H);
                    w.AddTensor(p + "ffn_down_shexp.weight", ty, TinyModels.Matrix(rng, ty, E, H, 0.06f), H, E);
                }
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

        static byte[] Vec(Rng rng, int n, float scale)
        {
            float[] v = new float[n];
            for (int i = 0; i < n; i++) v[i] = scale * rng.Normal();
            return F32(v);
        }
    }
}

// Model shape from GGUF metadata, for the decoder-only families Windlass runs. C# 5 only.
// Key names and defaults follow llama.cpp's llama-model.cpp (MIT).
using System;
using System.Collections.Generic;

namespace Windlass
{
    public sealed class ModelConfig
    {
        public static readonly string[] Architectures = { "llama", "qwen2", "qwen3" };

        public string Arch;
        public int Layers, Embd, Heads, KvHeads, HeadDim, Vocab, ContextLength, RopeDims;
        public float RmsEps, RopeBase, RopeFreqScale = 1f;
        public bool RopeNeox;          // rotate (i, i + n/2) pairs instead of adjacent ones
        public bool QkNorm;            // Qwen3: RMSNorm on each Q and K head before RoPE
        public bool TiedEmbeddings;    // no output.weight: the head reuses token_embd
        public string Name;
        public readonly List<string> Warnings = new List<string>();

        public int QDim { get { return Heads * HeadDim; } }
        public int KvDim { get { return KvHeads * HeadDim; } }

        public static ModelConfig From(GgufModelFile g)
        {
            ModelConfig c = new ModelConfig();
            c.Arch = g.GetString("general.architecture", "");
            if (Array.IndexOf(Architectures, c.Arch) < 0)
                throw new NotSupportedException("Model architecture '" + c.Arch + "' is not supported. Windlass runs " + string.Join(", ", Architectures) + " (Llama 2/3, Mistral, Qwen2/2.5, Qwen3 dense models).");
            string a = c.Arch + ".";
            c.Name = g.GetString("general.name", "");
            c.Layers = (int)g.GetInt(a + "block_count");
            c.Embd = (int)g.GetInt(a + "embedding_length");
            c.Heads = (int)g.GetInt(a + "attention.head_count");
            c.KvHeads = (int)g.GetInt(a + "attention.head_count_kv", c.Heads);
            c.HeadDim = (int)g.GetInt(a + "attention.key_length", c.Embd / c.Heads);
            c.RopeDims = (int)g.GetInt(a + "rope.dimension_count", c.HeadDim);
            c.ContextLength = (int)g.GetInt(a + "context_length", 4096);
            c.RmsEps = g.GetFloat(a + "attention.layer_norm_rms_epsilon", 1e-5f);
            c.RopeBase = g.GetFloat(a + "rope.freq_base", 10000f);
            c.RopeNeox = c.Arch == "qwen2" || c.Arch == "qwen3";
            c.QkNorm = g.TensorsByName.ContainsKey("blk.0.attn_q_norm.weight");
            c.TiedEmbeddings = !g.TensorsByName.ContainsKey("output.weight");

            string scaling = g.GetString(a + "rope.scaling.type", "none");
            float factor = g.GetFloat(a + "rope.scaling.factor", 0f);
            if (scaling == "linear" && factor > 0) c.RopeFreqScale = 1f / factor;
            else if (scaling != "none" && factor > 0 && factor != 1f)
                c.Warnings.Add("RoPE scaling '" + scaling + "' is not implemented; prompts past the original training length may degrade.");
            if (g.Has(a + "expert_count") && g.GetInt(a + "expert_count") > 0)
                throw new NotSupportedException("Mixture-of-experts models are not supported yet.");
            if (c.Heads % c.KvHeads != 0) throw new NotSupportedException("head_count must be a multiple of head_count_kv.");
            if (c.RopeDims > c.HeadDim || c.RopeDims % 2 != 0) throw new NotSupportedException("Unsupported rope.dimension_count " + c.RopeDims + ".");

            GgufTensor embd;
            if (!g.TensorsByName.TryGetValue("token_embd.weight", out embd)) throw new NotSupportedException("The model has no token_embd.weight.");
            c.Vocab = embd.Rows;
            for (int l = 0; l < c.Layers; l++)
                foreach (string n in RequiredLayerTensors)
                    if (!g.TensorsByName.ContainsKey("blk." + l + "." + n + ".weight"))
                        throw new NotSupportedException("Layer " + l + " has no " + n + ".weight (unsupported layout).");
            foreach (GgufTensor t in g.Tensors)
                if (t.Name.EndsWith(".weight") && t.Dims.Length >= 2 && !Quants.CanDecode(t.Type))
                    throw new NotSupportedException("Tensor " + t.Name + " uses " + GgmlTypes.Name(t.Type) + ", which Windlass cannot decode yet. Use a Q8_0, Q6_K, Q5_K_M, Q4_K_M, Q3_K_M or Q2_K file.");
            return c;
        }

        public static readonly string[] RequiredLayerTensors =
            { "attn_norm", "attn_q", "attn_k", "attn_v", "attn_output", "ffn_norm", "ffn_gate", "ffn_up", "ffn_down" };
    }
}

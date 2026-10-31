// Model shape from GGUF metadata, for the decoder-only families Windlass runs. C# 5 only.
// Key names and defaults follow llama.cpp's llama-model.cpp (MIT).
using System;
using System.Collections.Generic;

namespace Windlass
{
    public sealed class ModelConfig
    {
        public static readonly string[] Architectures = { "llama", "qwen2", "qwen3", "mixtral", "qwen2moe", "qwen3moe", "granite", "olmo2" };

        public string Arch;
        public int Layers, Embd, Heads, KvHeads, HeadDim, Vocab, ContextLength, RopeDims;
        public float RmsEps, RopeBase, RopeFreqScale = 1f;
        public bool RopeNeox;          // rotate (i, i + n/2) pairs instead of adjacent ones
        public bool QkNorm;            // Qwen3: RMSNorm on each Q and K head before RoPE
        public bool TiedEmbeddings;    // no output.weight: the head reuses token_embd
        // Granite (llama graph plus four scalars, 0 = not set, as in llama.cpp): the embeddings are multiplied by
        // EmbeddingScale, each residual branch by ResidualScale, attention scores use AttentionScale instead of
        // 1/sqrt(head dim), and the logits are divided by LogitScale.
        public float EmbeddingScale, ResidualScale, AttentionScale, LogitScale;
        // OLMo 2: no norm before attention or the FFN; the attention and FFN outputs are normalised before the residual
        // add, and Q and K are normalised over their whole vectors (not per head).
        public bool PostNorms;
        public bool QkNormFull;
        // Mixture of experts (llama.cpp build_moe_ffn): a router scores ExpertCount experts per token, the ExpertUsed best
        // run and their outputs are mixed. A layer is MoE when it has ffn_gate_inp.weight (Qwen3 may keep some dense).
        public int ExpertCount, ExpertUsed, ExpertFf, SharedExpertFf;
        public bool ExpertWeightsNorm; // renormalise the selected router weights to sum to 1
        public bool[] MoeLayers;
        public bool Moe { get { return ExpertCount > 0; } }
        public string Name;
        public readonly List<string> Warnings = new List<string>();

        public int QDim { get { return Heads * HeadDim; } }
        public int KvDim { get { return KvHeads * HeadDim; } }

        public static ModelConfig From(GgufModelFile g)
        {
            ModelConfig c = new ModelConfig();
            c.Arch = g.GetString("general.architecture", "");
            if (Array.IndexOf(Architectures, c.Arch) < 0)
                throw new NotSupportedException("Model architecture '" + c.Arch + "' is not supported. Windlass runs " + string.Join(", ", Architectures) + " (Llama 2/3, Mistral, Mixtral, Qwen2/2.5, Qwen3 dense; Qwen2-MoE and Qwen3-MoE; Granite 3 dense; OLMo 2).");
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
            c.RopeNeox = c.Arch == "qwen2" || c.Arch == "qwen3" || c.Arch == "qwen2moe" || c.Arch == "qwen3moe" || c.Arch == "olmo2";
            if (c.Arch == "granite")
            {
                c.EmbeddingScale = g.GetFloat(a + "embedding_scale", 0f);
                c.ResidualScale = g.GetFloat(a + "residual_scale", 0f);
                c.AttentionScale = g.GetFloat(a + "attention.scale", 0f);
                c.LogitScale = g.GetFloat(a + "logit_scale", 0f);
            }
            c.PostNorms = c.Arch == "olmo2";
            c.QkNormFull = c.Arch == "olmo2";
            c.QkNorm = g.TensorsByName.ContainsKey("blk.0.attn_q_norm.weight");
            c.TiedEmbeddings = !g.TensorsByName.ContainsKey("output.weight");

            string scaling = g.GetString(a + "rope.scaling.type", "none");
            float factor = g.GetFloat(a + "rope.scaling.factor", 0f);
            if (scaling == "linear" && factor > 0) c.RopeFreqScale = 1f / factor;
            else if (scaling != "none" && factor > 0 && factor != 1f)
                c.Warnings.Add("RoPE scaling '" + scaling + "' is not implemented; prompts past the original training length may degrade.");
            c.ExpertCount = (int)g.GetInt(a + "expert_count", 0);
            if (c.ExpertCount > 0 && c.Arch != "llama" && c.Arch != "mixtral" && c.Arch != "qwen2moe" && c.Arch != "qwen3moe")
                throw new NotSupportedException("Mixture-of-experts layers are supported for llama/mixtral, qwen2moe and qwen3moe, not '" + c.Arch + "'.");
            if (c.ExpertCount == 0 && (c.Arch == "qwen2moe" || c.Arch == "qwen3moe"))
                throw new NotSupportedException(c.Arch + " needs " + a + "expert_count.");
            if (c.ExpertCount > 0)
            {
                c.ExpertUsed = (int)g.GetInt(a + "expert_used_count", 0);
                if (c.ExpertUsed < 1 || c.ExpertUsed > c.ExpertCount)
                    throw new NotSupportedException(a + "expert_used_count " + c.ExpertUsed + " must be 1.." + c.ExpertCount + ".");
                // llama.cpp: Mixtral and Qwen3-MoE renormalise the chosen weights, Qwen2-MoE does not; the GGUF key wins when set.
                bool norm = c.Arch != "qwen2moe";
                if (g.Has(a + "expert_weights_norm")) norm = Convert.ToBoolean(g.Metadata[a + "expert_weights_norm"].Value);
                c.ExpertWeightsNorm = norm;
            }
            if (c.Heads % c.KvHeads != 0) throw new NotSupportedException("head_count must be a multiple of head_count_kv.");
            if (c.RopeDims > c.HeadDim || c.RopeDims % 2 != 0) throw new NotSupportedException("Unsupported rope.dimension_count " + c.RopeDims + ".");

            GgufTensor embd;
            if (!g.TensorsByName.TryGetValue("token_embd.weight", out embd)) throw new NotSupportedException("The model has no token_embd.weight.");
            c.Vocab = embd.Rows;
            c.MoeLayers = new bool[c.Layers];
            for (int l = 0; l < c.Layers; l++)
            {
                RequireLayer(g, l, c.PostNorms ? Olmo2LayerTensors : RequiredLayerTensors);
                c.MoeLayers[l] = c.ExpertCount > 0 && g.TensorsByName.ContainsKey("blk." + l + ".ffn_gate_inp.weight");
                if (c.MoeLayers[l]) c.CheckMoeLayer(g, l);
                else RequireLayer(g, l, DenseFfnTensors);
            }
            if (c.QkNormFull)
                for (int l = 0; l < c.Layers; l++)
                    if (g.TensorsByName["blk." + l + ".attn_q_norm.weight"].ElementCount != c.QDim || g.TensorsByName["blk." + l + ".attn_k_norm.weight"].ElementCount != c.KvDim)
                        throw new NotSupportedException("Layer " + l + ": attn_q_norm/attn_k_norm must cover the whole Q and K vectors (" + c.QDim + " and " + c.KvDim + ").");
            foreach (GgufTensor t in g.Tensors)
                if (t.Name.EndsWith(".weight") && t.Dims.Length >= 2 && !Quants.CanDecode(t.Type))
                    throw new NotSupportedException("Tensor " + t.Name + " uses " + GgmlTypes.Name(t.Type) + ", which Windlass cannot decode yet. Use a Q8_0, Q6_K, Q5_K_M, Q4_K_M, Q3_K_M, Q2_K or I-quant (IQ4_NL, IQ4_XS, IQ3_XXS, IQ3_S, IQ2_XXS, IQ2_XS, IQ2_S, IQ1_S, IQ1_M) file.");
            return c;
        }

        // Every layer needs these; then either the dense feed-forward or, in MoE layers, the router and stacked experts.
        public static readonly string[] RequiredLayerTensors = { "attn_norm", "attn_q", "attn_k", "attn_v", "attn_output", "ffn_norm" };
        // OLMo 2 has no attn_norm or ffn_norm; it norms the attention and FFN outputs and Q and K instead.
        public static readonly string[] Olmo2LayerTensors = { "attn_q", "attn_k", "attn_v", "attn_output", "attn_q_norm", "attn_k_norm", "post_attention_norm", "post_ffw_norm" };
        public static readonly string[] DenseFfnTensors = { "ffn_gate", "ffn_up", "ffn_down" };
        public static readonly string[] MoeFfnTensors = { "ffn_gate_inp", "ffn_gate_exps", "ffn_up_exps", "ffn_down_exps" };
        public static readonly string[] SharedExpertTensors = { "ffn_gate_shexp", "ffn_up_shexp", "ffn_down_shexp" };

        static void RequireLayer(GgufModelFile g, int l, string[] names)
        {
            foreach (string n in names)
                if (!g.TensorsByName.ContainsKey("blk." + l + "." + n + ".weight"))
                    throw new NotSupportedException("Layer " + l + " has no " + n + ".weight (unsupported layout"
                        + (n.EndsWith("_exps") ? "; Windlass reads the stacked expert tensors, not per-expert ffn_gate.N.weight files" : "") + ").");
        }

        // The router and the stacked experts ([cols, rows, experts]) must have the shapes the forward pass indexes with.
        void CheckMoeLayer(GgufModelFile g, int l)
        {
            RequireLayer(g, l, MoeFfnTensors);
            string p = "blk." + l + ".";
            GgufTensor router = g.TensorsByName[p + "ffn_gate_inp.weight"];
            GgufTensor gate = g.TensorsByName[p + "ffn_gate_exps.weight"], up = g.TensorsByName[p + "ffn_up_exps.weight"], down = g.TensorsByName[p + "ffn_down_exps.weight"];
            if (router.Cols != Embd || router.Rows != ExpertCount)
                throw new NotSupportedException("Layer " + l + ": ffn_gate_inp.weight must be " + Embd + " x " + ExpertCount + ".");
            if (gate.Dims.Length != 3 || gate.Dims[2] != ExpertCount || gate.Cols != Embd || up.Dims.Length != 3 || up.Dims[2] != ExpertCount || up.Cols != Embd || up.Dims[1] != gate.Dims[1])
                throw new NotSupportedException("Layer " + l + ": ffn_gate_exps/ffn_up_exps must be " + Embd + " x ff x " + ExpertCount + ".");
            if (down.Dims.Length != 3 || down.Dims[2] != ExpertCount || down.Dims[1] != Embd || down.Dims[0] != gate.Dims[1])
                throw new NotSupportedException("Layer " + l + ": ffn_down_exps must be ff x " + Embd + " x " + ExpertCount + ".");
            ExpertFf = (int)gate.Dims[1];
            if (g.TensorsByName.ContainsKey(p + "ffn_gate_shexp.weight") || g.TensorsByName.ContainsKey(p + "ffn_up_shexp.weight") || g.TensorsByName.ContainsKey(p + "ffn_down_shexp.weight"))
            {
                RequireLayer(g, l, SharedExpertTensors);
                GgufTensor sg = g.TensorsByName[p + "ffn_gate_shexp.weight"], su = g.TensorsByName[p + "ffn_up_shexp.weight"], sd = g.TensorsByName[p + "ffn_down_shexp.weight"];
                if (sg.Cols != Embd || su.Cols != Embd || sg.Rows != su.Rows || sd.Rows != Embd || sd.Cols != sg.Rows)
                    throw new NotSupportedException("Layer " + l + ": shared expert tensors have inconsistent shapes.");
                SharedExpertFf = sg.Rows;
                GgufTensor sgate;
                if (g.TensorsByName.TryGetValue(p + "ffn_gate_inp_shexp.weight", out sgate) && (sgate.Cols != Embd || sgate.Rows != 1))
                    throw new NotSupportedException("Layer " + l + ": ffn_gate_inp_shexp.weight must have " + Embd + " values.");
                if (sgate == null && Arch == "qwen2moe") throw new NotSupportedException("Layer " + l + " has no ffn_gate_inp_shexp.weight (unsupported layout).");
            }
            else if (Arch == "qwen2moe") throw new NotSupportedException("Layer " + l + " has no ffn_gate_shexp.weight (unsupported layout).");
        }
    }
}

# How Windlass works

1. **Read the index, not the model.** `Gguf.cs` reads only the GGUF header: metadata, tokenizer and the offset of
   every tensor. Nothing else is loaded at start.
2. **Stages.** Each decoder layer (`blk.N.*`) is one stage, and the final norm plus output head is the last one
   (`LayerStream.cs`).
3. **Stream layers.** Just before a stage runs, its tensors are read from disk into one of two RAM slots. While it
   computes, a worker thread reads the next stage into the other slot (prefetch). The embedding table is never
   loaded: each token's row is read on its own. `--cache-gb` keeps the first stages that fit resident instead.
4. **Whole prompt per pass.** The prompt runs through each layer as one batch, so a prompt costs one pass over the
   file, not one per token. Each new token then costs one more pass.
5. **Keep the KV cache.** Keys and values stay in RAM across layers, tokens and requests (`Transformer.cs`), and a
   repeated prompt prefix is reused.
6. **Compute.** Weights stay in their GGUF format on disk and in RAM and are decoded on the fly (`Quants.cs`, a port
   of llama.cpp's reference decoders). Single tokens use fused decode-and-dot kernels; prompts decode each tile of
   rows once and reuse it for every token in a register-blocked kernel (`FastMath.cs`). `--int-dot` instead quantizes
   the activations to Q8 once and uses integer dot products (llama.cpp's CPU method). CPU: scalar C# with `Parallel.For` and unsafe pointers, like Phoenix's
   `CpuRef`.
7. **Text.** The tokenizer (`Tokenizer.cs`: byte-level BPE for Llama 3 / Qwen / Mistral Tekken, SentencePiece for
   Llama 2 / Mistral) and the chat template (`ChatTemplate.cs`, recognised from the GGUF's Jinja template) are built
   in, so no Python or Jinja engine is needed.

## Speed

Every token reads the whole model once, unless it is cached. Windows' file cache helps a lot when the model fits in
RAM: after the first pass, most reads come from memory instead of the drive.

Measured on 2026-10-27, Ryzen 9 9950X3D (16 cores, 32 threads), 96 GB RAM, Samsung 9100 PRO (PCIe 5.0 NVMe), CPU only:

| Model | File | Run | Result |
| --- | --- | --- | --- |
| Llama 3.2 3B Instruct Q8_0 | 3.4 GB | `run`, 21 prompt + 36 reply tokens | 27.3 s, about 0.75 s per token |
| Llama 3.3 70B Instruct Q4_K_M | 42.5 GB | `run`, 21 prompt + 40 reply tokens | 349.3 s, about 8.7 s per token |
| Llama 3.3 70B Instruct Q4_K_M | 42.5 GB | through Fairlead's `deep-local` route, 20 + 30 tokens | 264.8 s, about 8.8 s per token |

A short paragraph from a 70B model takes 5 to 10 minutes. Prompt processing is the slow part on a CPU (the same
multiply-adds for every prompt token), so keep prompts short or let the prefix cache carry earlier turns.
`Launch-Windlass.bat run` prints the time per reply and the bytes read, so you can measure your own machine.

## Where it fits next to Phoenix LAI

[Phoenix LAI](https://github.com/fleethacker-data-crucible/phoenix-lai) already proves the stack: GGUF models, a
tokenizer, the forward pass and an OpenAI API, all in C# 5 compiled in memory by PowerShell 5.1, with zero
dependencies. Windlass is built the same way, borrows Phoenix's half-float and JSON code, and uses the suite's port
helper.

| | Phoenix LAI | Windlass |
| --- | --- | --- |
| Models | Gemma 4, loaded whole into VRAM | Llama 2/3, Mistral, Qwen2/2.5, Qwen3, streamed from disk |
| Model size | what fits on the GPU | bigger than RAM: only about two layers are in memory at once |
| Speed | interactive chat | seconds to minutes per token |
| Compute | Vulkan GPU | CPU (v0.2) |
| API | `http://127.0.0.1:1234/v1` | `http://127.0.0.1:8777/v1` |

Use Phoenix for everyday work. Use Windlass when a task needs a model nothing else on the machine can run, and you
can wait.

## Roadmap

- **GPU offload (Vulkan).** Stream each layer into VRAM and run it with the SPIR-V kernels Phoenix already
  generates in C#, keeping the zero-dependency rule. This turns prompt processing from the slow part into the fast
  part.
- fp16 KV cache, I-quants and mixture-of-experts layers.
- More architectures, one family at a time (for example `qwen35`, Qwen 3.5's hybrid of attention and recurrent layers).

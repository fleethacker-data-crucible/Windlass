# Windlass speed and efficiency plan

Written 2026-10-28 from a review of the v0.2 source. Nothing here is implemented yet. The gains are estimates from
reading the code, not measurements; confirm each one with a timed `run` before and after.

## Target hardware: unknown, so measure it

The afloat setup is not known. Do **not** tune for the dev desktop (Ryzen 9 9950X3D, 96 GB RAM, PCIe 5.0 NVMe): there
a 70B Q4_K_M (42.5 GB) fits in the Windows file cache, which no fielded machine is likely to match.

Two cases to design for:

| Case | Likely machine | What limits speed |
| --- | --- | --- |
| **Streamed from disk** | HP ZBook laptop: 32 GB RAM, NVIDIA GPU (4-16 GB VRAM) plus Intel graphics, NVMe of unknown speed, probably BitLocker and endpoint security, may run on battery | Bytes read per token / disk speed. A 70B file does not fit in RAM, so every token reads most of it from the drive. Prompt processing on a throttled laptop CPU may cost as much as the disk. |
| **Fits in RAM** | A shipboard server VM (CANES), if one can be had: RAM, cores and GPUs unknown | Memory bandwidth and CPU math. Several users share it, so batching matters. |

Rule: Windlass must measure where it runs and set itself up (item 1), so one build serves both cases.

Open questions for the ship's IT / CANES program office (they decide whether the server case is real):
1. Can a mission app get a VM, and with how much RAM, how many cores and what storage?
2. Are there GPUs or accelerators afloat, now or planned?
3. What accreditation (ATO) path would a local LLM server need?

Serving other machines would also mean dropping loopback-only binding (with auth and TLS), which is a decision for the
owner, not a tuning change. Until then a server instance is reached the same way as today, through Fairlead.

## Current bottlenecks found in the code

- `LayerStream.Read` (`src/LayerStream.cs`): every stage is copied twice (file cache -> 8 MB managed buffer ->
  unmanaged slot with `Marshal.Copy`), on one thread, one tensor at a time. On the desktop that is about 85 GB of
  copying per 70B token; measured 8.7 s/token there is about 5 GB/s against far higher RAM bandwidth.
- Streaming a file bigger than the file cache in order is the cache's worst case: each layer evicts the one needed
  next. `--cache-gb` keeps its own copy while Windows caches the same bytes again.
- `Transformer.MatMul` (`src/Transformer.cs`): for prompts, each tile of rows is decoded once, then each token does a
  separate float dot product per row (no register blocking). For single tokens, `Quants.DotRow` converts every
  quant to float (no integer dot).
- `Transformer.Layer`: allocates about 8 large arrays per layer per call, decodes the norm vectors (`Vector`) and
  recomputes `RopeTable` with trig for every layer and token.
- KV cache is fp32 (70B: 640 KB per token, 2.5 GB at 4k context) and `Reserve` copies every layer's cache each time
  it doubles.
- `Server.cs` scheduler runs one request at a time; a queued request waits for a full extra pass per token even
  though a pass costs nearly the same for 1 or 8 sequences.

## The improvements and expected wins

| # | Improvement | Fits in RAM | Streamed from disk | Cost / catch |
| --- | --- | --- | --- | --- |
| 1 | **Probe command**: RAM, VRAM, sustained disk read (under BitLocker), cores, AC vs battery; times one layer pass and predicts s/token for a model | none itself; picks settings | same | small; do first |
| 2 | **Memory-mapped weights** (`System.IO.MemoryMappedFiles`, no double copy) | **2-4x** | ~1.1-1.3x | moderate rewrite of `LayerStream` |
| 3 | **Parallel reads that skip the file cache** (several `FileStream`s per stage so NVMe gets queue depth) | n/a | ~1.2-2x, drive dependent | pairs with 4 |
| 4 | **Pin part of the model in RAM, sized from the probe**; stream the rest unbuffered | n/a (all pinned) | saves the pinned share: 18 of 42.5 GB pinned = ~40% fewer reads, **~1.7x** | RAM; item 11 frees more |
| 5 | **Pin layers in VRAM** | n/a | 8 GB VRAM = ~20% fewer reads, ~1.2x | needs the GPU exception (15) |
| 6 | **Smaller quants** (IQ2_XXS, IQ2_S, IQ3_S) | ~1.5-2x | **~2x**; a ~19 GB 2-bit 70B may fit in 32 GB RAM, moving it to the left column | real quality loss at 2-bit |
| 7 | **Prompt-lookup speculation**: guess the next tokens from text already in the context, check them in one batched pass | 1x on free text, **1.5-3x** on edits, summaries, code | same | free; port from Phoenix |
| 8 | **Draft-model speculation** (e.g. Llama 3.2 1B drafting for Llama 3.3 70B, same tokenizer) | ~2-3x | ~2-3x | ~1 GB RAM plus draft compute; tokenizers must match |
| 9 | **Batch queued requests through one pass** (the core AirLLM idea Windlass lacks) | **~Nx** throughput for N requests | same | no single request gets faster; mainly for a shared server |
| 10 | **MoE models** (e.g. Qwen3-30B-A3B ~18 GB, fits a 32 GB laptop; Qwen3-235B-A22B reads ~22B params per token) | 3-10x vs dense of similar quality | same or more | new architecture work |
| 11 | **fp16 KV cache**, grow without copying every layer | half the KV memory | frees ~1.3 GB at 4k for item 4 | small |
| 12 | **Blocked matmul for prompts** (4 rows x 4 tokens in registers) | **2-4x prompt processing** | same | no help when generating |
| 13 | **Integer dot products**: quantize activations to Q8 once per row, integer multiply-add | ~1.5-2x per generated token | only when the CPU is slower than the disk | moderate |
| 14 | **Per-layer waste**: precompute RoPE tables, decode norms once per stage, reuse buffers | a few % | ~0 | trivial |
| 15 | **GPU via Vulkan** (Phoenix's SPIR-V kernels) | **10x+ prompt processing**, faster generation | prompt 10x+; generation still disk bound | needs owner approval for a `vulkan-1.dll` P/Invoke exception |

Gains only multiply when they hit different bottlenecks:
- **Laptop, streamed:** 4 + 6 + 7/8 (+ 3). Pinning, a smaller quant and speculation could take a 70B from minutes to
  tens of seconds per token. CPU items barely matter.
- **Fits in RAM:** 2 + 12 + 13 + 9.
- Also consider whether a fielded laptop should run a 70B at all: a 30B dense or a MoE model that fits in RAM may be
  the realistic target. The probe settles it.

## Order of work

1. Item 1 (probe). Everything else is tuned from its numbers.
2. Cheap wins on any machine: 7, 11, 14.
3. Read path: 2, then 3 + 4 together (auto-sized pin).
4. 12 (prompts), 6 (IQ quants), 8 (draft model).
5. Last: 13, 9 (waits on the server question), 10, then 5 + 15 (wait on the Vulkan approval).

## Code to reuse from Phoenix LAI

Same rules (C# 5, .NET 4.8 CodeDom, PowerShell 5.1, no third-party code), so these port directly:

| Need | Phoenix file |
| --- | --- |
| Memory-mapped GGUF | `src/Gguf.cs` (`MemoryMappedFile.CreateFromFile`, `AcquirePointer`) |
| Prompt-lookup speculation | `src/Engine.cs` (`Speculate`, around the "prompt-lookup speculation" section and the generation loop's `guessing` logic; batched verification of up to 16 tokens) |
| Q8 activations for integer dots | `src/CpuRef.cs` (`QuantizeQ8_0`, Q8_K blocks) |
| IQ2_XXS / IQ2_S / IQ3_S | `src/CpuRefIQuants.cs`, `src/IqTables.cs` |
| fp16 KV, Vulkan kernels | `src/GemmaGpu.cs`, `src/Kernels*.cs`, `src/SpirvBuilder.cs`, `src/Vulkan.cs` |
| Probe pattern (read-only scripts, one zip back) | `nvidia/` (`1-check.ps1`, `2-probe.ps1`, `3-collect.ps1`) |

## Constraints (do not break)

- C# 5 only (no `=>` members, `$""`, `?.`, `nameof`, tuples, `Span<T>`, `System.Numerics.Vector<T>`); scalar code
  with `Parallel.For` and unsafe pointers.
- `src/Start-Windlass.ps1` refuses `Process.Start`, `DllImport`, `Assembly.Load`, `NativeLibrary` and
  `Marshal.GetDelegateForFunctionPointer`. `MemoryMappedFiles` is allowed. The GPU items need an explicit owner
  approval and a guard change, as Phoenix has for `vulkan-1.dll`.
- Loopback only, no outgoing connections, models from local paths only.
- Keep results matching `tests/fixtures/llama-cpp-reference.txt`; run `tests\Run-Tests.ps1` under Windows
  PowerShell 5.1 after every change.

## How to measure

For each change, on the same machine and model, before and after:
`Launch-Windlass.bat run --model <file> --prompt "<same prompt>"` and record the total time, seconds per token and
bytes read it prints. Add the results to the Speed table in [ARCHITECTURE.md](ARCHITECTURE.md#speed), naming the
machine (RAM, drive, CPU, AC or battery).

## Repo housekeeping found at the same time

- `fleet-gate` fails on `src/FleetPort.psm1:261`: the `house` check flags `HTTP://*:5357` and `HTTP://+:80`, which are
  only examples in a comment about `netsh` output, not listeners. Reword the comment or teach the gate to skip comments.
- Issue #5: `main` has no workflow, so nothing is tested in CI. PR #2 (`ci/fleet-gate`) branched before #4 and would
  delete the docs and brand assets if merged as is; rebuild it on current `main` with only the workflow file.

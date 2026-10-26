<p align="center"><img src="assets/brand/emblem-light.png" width="280" alt="Windlass"></p>

# Windlass

> Run very large local models (70B and up) on an ordinary Windows PC by streaming them through memory one layer at a time, behind an OpenAI-compatible API.

![PS 5.1](https://img.shields.io/badge/PowerShell-5.1-5391FE) ![No admin](https://img.shields.io/badge/admin-not%20required-3a7d44) ![No installs](https://img.shields.io/badge/installs-none-3a7d44) ![License MIT](https://img.shields.io/badge/license-MIT-blue)

Part of the [FleetHacker Edge-Ops suite](https://github.com/fleethacker-data-crucible/edge-ops).

## The name

A *windlass* is the heavy winch at a ship's bow that hauls the anchor chain in link by link. It is slow, but it lifts what nothing else on board can. Windlass does the same with a huge model: it hauls the model through memory one layer at a time, so a PC that could never hold the whole model can still run it.

**Status: v0.2, CPU only.** Windlass is now C# on Windows PowerShell 5.1, the Fleet workstation stack, with no Python and no third-party code. The engine is checked against llama.cpp on every supported weight type and architecture, and the HTTP API against the contract below; see [Tests](#development-and-tests). It has not yet been timed on a real 70B model. Expect seconds to minutes per token on big models, and slow prompt processing: this is for batch and deep work, not chat. GPU offload is the next step (see [Roadmap](#roadmap)).

## Where it fits next to Phoenix LAI

[Phoenix LAI](https://github.com/fleethacker-data-crucible/phoenix-lai) already proves the stack: GGUF models, a tokenizer, the forward pass and an OpenAI API, all in C# 5 compiled in memory by PowerShell 5.1, with zero dependencies. Windlass is built the same way, borrows Phoenix's half-float and JSON code, and uses the suite's port helper. The two do different jobs:

| | Phoenix LAI | Windlass |
| --- | --- | --- |
| Models | Gemma 4, loaded whole into VRAM | Llama 2/3, Mistral, Qwen2/2.5, Qwen3, streamed from disk |
| Model size | what fits on the GPU | bigger than RAM: only about two layers are in memory at once |
| Speed | interactive chat | seconds to minutes per token |
| Compute | Vulkan GPU | CPU (v0.2) |
| API | `http://127.0.0.1:1234/v1` | `http://127.0.0.1:8777/v1` |

Use Phoenix for everyday work. Use Windlass when a task needs a model nothing else on the machine can run, and you can wait.

## Run (whitelisted Windows)

1. Download or clone the folder, then **Unblock** it if it came as a zip.
2. Put a GGUF model somewhere local, for example `D:\models\Llama-3.3-70B-Instruct-Q4_K_M.gguf`. Windlass reads files only; it never downloads anything.
3. In a console:

   ```bat
   Windlass.bat serve --model D:\models\Llama-3.3-70B-Instruct-Q4_K_M.gguf
   ```

The console shows the log. The API answers on **`http://127.0.0.1:8777/v1`** (the first free port in Windlass's block, 8777-8779). While the model loads, chat requests get `503 model_loading`. Stop it with **Ctrl+C** or `src\Stop-Windlass.ps1`.

Two more commands help before you commit to a long run:

```bat
Windlass.bat info --model D:\models\model.gguf                  :: shape, file sizes, RAM per layer, KV cache per token
Windlass.bat run  --model D:\models\model.gguf --prompt "Hi"    :: one reply in the console, no server
```

For a split model (`name-00001-of-00004.gguf`), give any one part.

| Option | Default | What it does |
| --- | --- | --- |
| `--model` | (required) | The GGUF file. |
| `--model-id` | file name | The id published in `/v1/models`. |
| `--port` | 8777-8779 | A fixed port instead of the first free one in the block. |
| `--api-key` | `$env:WINDLASS_API_KEY` | Require `Authorization: Bearer <key>` (or `X-Api-Key`) on `/v1`. |
| `--context-length` | model's, at most 8192 | Context to allow. The KV cache grows with use, up to this. |
| `--cache-gb` | `0` | Keep the first layers that fit in this many GB resident, so they are read from disk once. |
| `--no-prefetch` | off | Do not read the next layer while the current one computes (saves one layer of RAM). |
| `--chat-template` | detected | `llama3`, `chatml`, `mistral`, `mistral-v7` or `llama2`. |
| `--max-tokens` | `1024` | `max_tokens` for requests that give none. |
| `--max-queue` | `8` | Requests that may wait behind the running one; more get `503 server_busy`. |
| `--keepalive` | `15` | Seconds between SSE `: keepalive` comments (under 60). |
| `--stub` | off | Serve a canned reply with no model, to test wiring. |
| `--verbose` | off | Log every layer. |

## Requirements

| Needs | |
| --- | --- |
| Windows PowerShell 5.1 + .NET Framework 4.8 | Yes (built in) |
| Admin rights | No |
| Installs, Python, third-party runtimes or DLLs | No |
| Network access | No. Loopback only; nothing leaves the machine. |
| Runtime C# compile (`Add-Type`) | Yes, from `src\*.cs` on every start |
| GPU | No (CPU only in v0.2) |
| RAM | About two layers plus the KV cache: roughly 2 GB plus 0.66 MB per token of context for a 70B Q4_K_M model (`info` prints the numbers) |
| Disk | The GGUF file, on the fastest drive you have |
| Default port | 8777-8779, policy `exclusive` ([PORTS.md](https://github.com/fleethacker-data-crucible/edge-ops/blob/main/PORTS.md)) |

**Models.** GGUF files of the `llama` (Llama 2, Llama 3.x, Mistral 7B, Mistral Large and other Llama-layout models), `qwen2` (Qwen2, Qwen2.5) and `qwen3` (dense Qwen3) architectures, in F32, F16, BF16, Q8_0, Q5_0, Q5_1, Q4_0, Q4_1, Q6_K, Q5_K, Q4_K, Q3_K or Q2_K (so the usual `Q8_0`, `Q6_K`, `Q5_K_M`, `Q4_K_M`, `Q3_K_M` and `Q2_K` files). Not yet: mixture-of-experts models, I-quants (`IQ*`), vision models, other architectures (Gemma is Phoenix's job). Windlass says so and refuses them at load.

## The API

- **`POST /v1/chat/completions`**: OpenAI Chat Completions, `stream: false` or `true`. Streams are SSE `data: {chat.completion.chunk}` lines ending with `data: [DONE]`. `stream_options.include_usage` adds a final chunk with `usage`; non-stream replies always carry `usage`. Honours `max_tokens` / `max_completion_tokens`, `temperature` (0 = greedy), `top_p`, `stop` and `seed`. Message `content` may be a string or an array of `text` parts. `tools` are ignored (logged); Windlass does not do tool calling.
- **`GET /v1/models`** and **`GET /v1/models/{id}`**: `{"object": "list", "data": [{"id": ..., "context_length": N, ...}]}`.
- **`GET /health`**: `status` (`loading`, `ready`, `error`), `busy`, `queued`.
- **`GET /__fleethacker/id`** and **`POST /__fleethacker/shutdown`**: the suite's self-detection endpoints.

Errors are `{"error": {"message": ..., "type": ...}}` with real status codes: 400 for a bad request or an over-long prompt (`context_length_exceeded`), 401 for a wrong key, 404 for an unknown model, 503 while loading (`Retry-After: 30`), after a failed load, or when the queue is full (`Retry-After: 60`).

**Slow but alive.** A streaming request gets its headers and an `: accepted` comment as soon as it is accepted, then a `: keepalive` comment every 15 s while it waits in the queue or while a layer pass runs, so a router can tell slow from dead. **One at a time.** Windlass runs one generation at a time; others wait in a FIFO queue and the log shows each one's position. A streaming client that disconnects cancels its generation at the next layer, freeing the turn for the next request. **Follow-up turns are cheaper:** the KV cache of the previous prompt is kept, and a new prompt that starts the same way (the earlier turns of a chat) skips that part.

## Register it in Fairlead

1. In Fairlead, **Providers → Add provider**, choose the **Custom (OpenAI-compatible)** preset.
2. Base URL `http://127.0.0.1:8777/v1`. Add the API key if you set one.
3. Tick **Local**, and set the timeout to **3600** seconds.
4. Fetch models; Fairlead reads the id and context length from `/v1/models`.
5. **Routes → Add route** named `deep-local`, with the Windlass model as its only target, hedging off, and local fallback off. Do not add Windlass to the `local` route or the auto rules: it is far too slow for interactive traffic, so only requests that ask for `deep-local` by name should reach it.

Windlass binds loopback only, as the suite requires, so Fairlead runs on the same PC. (v0.1's `--lan` option is gone.)

## How it works

1. **Read the index, not the model.** `Gguf.cs` reads only the GGUF header: metadata, tokenizer and the offset of every tensor. Nothing else is loaded at start.
2. **Stages.** Each decoder layer (`blk.N.*`) is one stage, and the final norm plus output head is the last one (`LayerStream.cs`).
3. **Stream layers.** Just before a stage runs, its tensors are read from disk into one of two RAM slots. While it computes, a worker thread reads the next stage into the other slot (prefetch). The embedding table is never loaded: each token's row is read on its own. `--cache-gb` keeps the first stages that fit resident instead.
4. **Whole prompt per pass.** The prompt runs through each layer as one batch, so a prompt costs one pass over the file, not one per token. Each new token then costs one more pass.
5. **Keep the KV cache.** Keys and values stay in RAM across layers, tokens and requests (`Transformer.cs`), and a repeated prompt prefix is reused.
6. **Compute.** Weights stay in their GGUF format on disk and in RAM and are decoded on the fly (`Quants.cs`, a port of llama.cpp's reference decoders). Single tokens use fused decode-and-dot kernels; prompts decode each tile of rows once and reuse it for every token. CPU: scalar C# with `Parallel.For` and unsafe pointers, like Phoenix's `CpuRef`.
7. **Text.** The tokenizer (`Tokenizer.cs`: byte-level BPE for Llama 3 / Qwen / Mistral Tekken, SentencePiece for Llama 2 / Mistral) and the chat template (`ChatTemplate.cs`, recognised from the GGUF's Jinja template) are built in, so no Python or Jinja engine is needed.

**Speed, roughly.** Every token reads the whole model from disk once, unless it is cached. A 70B model at Q4_K_M is about 42 GB; on an NVMe drive reading 3 GB/s that is around 14 s per token for the reads alone, overlapped with compute by the prefetch. The CPU work is about 70 billion multiply-adds per token, so on a laptop compute rather than the disk may set the pace; `Windlass.bat run` prints the time per reply and the bytes read so you can measure your own machine. Prompt processing is the slow part on a CPU (the same multiply-adds for every prompt token), so keep prompts short or let the prefix cache carry earlier turns.

## Development and tests

```bat
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Run-Tests.ps1
```

`Run-Tests.ps1` compiles `src` and `tests` in memory and runs the C# suite (`tests\Tests.cs`); `tests\Windlass.Tests.ps1` wraps it for Pester 5, one Pester test per C# test. No model download is needed:

- **Engine.** `tests\TinyModels.cs` writes tiny random GGUF models (Llama, Qwen2 with biases, Qwen3 with QK norm, tied embeddings, Llama 3.1 `rope_freqs`, a SentencePiece vocabulary, a split file) in every supported weight type. Their logits are compared with llama.cpp's for the same files (`tests\fixtures\llama-cpp-reference.txt`). Streamed, prefetched and resident runs must give identical logits, token-by-token decoding must match a full pass, and the fused kernels must match decode-then-dot.
- **Tokenizer.** Token ids match llama.cpp's for Llama 3 BPE, Qwen2 BPE and SentencePiece vocabularies, including special tokens, Unicode and whitespace runs.
- **API.** A real server on loopback port 18777 (the suite's CI range) against a stub model checks the contract above: stream format, usage, error shapes and status codes, the API key, 503 while loading, early headers, keepalives while queued, the full-queue 503, cancel on disconnect, Host and Origin checks, and one end-to-end request through a real tiny model.

`Windlass.bat check` runs the locality checks and compiles the C# without starting anything. Lint with PSScriptAnalyzer and `PSScriptAnalyzerSettings.psd1`. All C# is C# 5 (the .NET Framework 4.8 CodeDom compiler) and ASCII.

## Security notes

- Loopback only: `HttpListener` on `http://127.0.0.1:<port>/` and `http://localhost:<port>/`. Every request's `Host` must be loopback; a browser `Origin` must be a loopback page (the `/__fleethacker/` endpoints accept only their own origin).
- No outgoing connections, no process starts, no P/Invoke, no dynamic assembly loading: `Start-Windlass.ps1` checks the source for these before it compiles and refuses to start if any appear.
- The API key is optional because only programs on this PC can reach the port. Set one if other users share the PC.
- Prompts and replies stay on the machine; the log records request ids, token counts and timings, not text.
- Model files are parsed defensively (bounds-checked header, known tensor types only), but load only models you trust.

## Roadmap

- **GPU offload (Vulkan).** Stream each layer into VRAM and run it with the SPIR-V kernels Phoenix already generates in C#, keeping the zero-dependency rule. This turns prompt processing from the slow part into the fast part.
- fp16 KV cache, I-quants and mixture-of-experts layers.

## License

MIT, see [LICENSE](LICENSE). Algorithms ported from llama.cpp (MIT) and parts of Phoenix LAI (MIT); the layer-streaming idea comes from AirLLM. See [THIRD-PARTY.md](THIRD-PARTY.md).

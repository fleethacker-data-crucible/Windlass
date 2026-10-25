<p align="center"><img src="assets/brand/emblem-light.png" width="280" alt="Windlass"></p>

# Windlass

**Run very large local models (70B and up) on a small GPU by loading one layer at a time, behind an OpenAI-compatible API.**

## The name

A *windlass* is the heavy winch at a ship's bow that hauls the anchor chain in link by link. It is slow, but it lifts what nothing else on board can. Windlass does the same with a huge model: it hauls the model through the GPU one layer at a time, so a card with a few gigabytes of memory can run a model that would never fit on it whole.

**Status: v0.1, first cut.** The engine, the HTTP API and the smoke test work end to end and are covered by automated tests. It has been run on CPU with small models; it has not yet been timed on a real 70B model on a GPU. Expect seconds to minutes per token on big models: this is for batch and deep work, not chat.

## Why Python, and where it runs

The Fleet workstation standard bans Python on the locked-down work PC. Windlass is the exception by design: it runs on a **GPU or build machine**, not the workstation. It needs PyTorch and Hugging Face Transformers, which have no practical non-Python equivalent. The workstation never runs it; [Fairlead](https://github.com/fleethacker-data-crucible/Fairlead) talks to it over HTTP like any other OpenAI-compatible local server, and needs no code for it.

## How it works

Windlass is a clean refactor of the [AirLLM](https://github.com/lyogavin/airllm) approach (see [THIRD-PARTY.md](THIRD-PARTY.md)):

1. **Split once.** The Hugging Face checkpoint is rewritten into one safetensors file per layer (embeddings, each decoder layer, final norm, output head). Splitting streams one layer through memory at a time, so a 140 GB checkpoint splits on a machine with far less RAM. The split is reused on every later start.
2. **Build the model on `meta`.** Transformers builds the model with no weights, so it costs no memory but still owns the forward pass: attention, rotary embeddings, masks and the KV cache stay correct as Transformers evolves.
3. **Stream layers.** Hooks load each layer's weights onto the GPU just before it runs and drop them just after. While one layer computes, a worker thread reads the next one from disk (prefetch). Layers up to 2 GB are pinned for faster copies to CUDA.
4. **Keep the KV cache.** A Transformers `DynamicCache` stays on the GPU across layers and across tokens, so each new token costs one pass over the layer files, not a re-read of the whole prompt.
5. **Optional compression.** `--compression 8bit` or `4bit` (NF4) stores layers compressed on disk, at about 0.53x and 0.28x the fp16 size. Disk reads are the bottleneck, so smaller files mean faster tokens. Weights are expanded back to fp16/bf16 on the GPU just before use. This is plain PyTorch, so unlike AirLLM's bitsandbytes route it also works on CPU, ROCm and Apple GPUs.

Supported model families: **Llama, Mistral, Qwen2 and Qwen3** Hugging Face checkpoints in safetensors format (for example Llama 3.x 70B, Qwen2.5 72B, Mistral Large). Others can be tried with `--allow-untested-arch`.

**Speed, roughly.** Every token reads the whole model from disk once. A 70B model at 4-bit is about 40 GB of shards; on an NVMe drive reading 3 GB/s that is around 13 s per token, plus the prompt pass. Put the shards on the fastest drive you have.

## Install

Python 3.10 or newer on the GPU machine.

```bash
python -m venv .venv
```

Install PyTorch for your GPU from [pytorch.org](https://pytorch.org/get-started/locally/) first (CUDA, ROCm, or CPU), then:

```bash
.venv/Scripts/python -m pip install -e ".[engine,dev]"
```

(On Linux the interpreter is `.venv/bin/python`.)

## Run it

```bash
.venv/Scripts/python -m windlass serve --model meta-llama/Llama-3.3-70B-Instruct --compression 4bit
```

`--model` takes a Hugging Face repo id (downloaded on first use; set `HF_TOKEN` for gated repos), a local checkpoint folder, or a folder of Windlass shards. The first start splits the checkpoint, which takes a while for a big model; later starts reuse the shards. Shards go to `%LOCALAPPDATA%\Windlass\shards` (Windows) or `~/.cache/windlass/shards`, or `$WINDLASS_SHARDS`, or `--shards-dir`. `windlass split --model ...` does the split without serving.

The server answers on **`http://127.0.0.1:8777/v1`** (8777 is Windlass's slot in the Fleet port registry, next after Fairlead's 8776). While the model loads, chat requests get `503 model_loading`.

| Option | Default | What it does |
| --- | --- | --- |
| `--port` | `8777` | Port to listen on. |
| `--lan` | off | Bind `0.0.0.0` so a Fairlead on another PC can reach this GPU box. Use with `--api-key`. |
| `--host` | `127.0.0.1` | Bind a specific address instead. |
| `--api-key` | `$WINDLASS_API_KEY` | Require `Authorization: Bearer <key>` on every request. |
| `--compression` | none | `8bit` or `4bit` layer shards. |
| `--device` | `auto` | `cuda`, `cuda:1`, `mps` or `cpu`. ROCm builds of PyTorch show up as `cuda`. |
| `--dtype` | `auto` | Compute dtype: bf16/fp16 on GPU, fp32 on CPU. |
| `--no-prefetch` | off | Do not read the next layer while the current one runs. |
| `--context-length` | from config | Override the context length advertised and enforced. |
| `--max-tokens` | `1024` | `max_tokens` for requests that give none. |
| `--max-queue` | `8` | Requests that may wait behind the running one; more get `503 server_busy`. |
| `--keepalive` | `15` | Seconds between SSE `: keepalive` comments (must be under 60). |
| `--model-id` | folder name | The id published in `/v1/models`. |
| `--stub` | off | Serve a canned reply with no model, to test wiring. |

## The API

- **`POST /v1/chat/completions`**: OpenAI Chat Completions, `stream: false` or `true`. Streams are SSE `data: {chat.completion.chunk}` lines ending with `data: [DONE]`. `stream_options.include_usage` adds a final chunk with `usage`; non-stream replies always carry `usage`. Honours `max_tokens` / `max_completion_tokens`, `temperature` (0 = greedy), `top_p`, `stop` and `seed`. `tools` are ignored (logged); Windlass does not do tool calling.
- **`GET /v1/models`**: `{"object": "list", "data": [{"id": ..., "context_length": N, ...}]}`.
- **`GET /health`**: `status` (`loading`, `ready`, `error`), `busy`, `queued`.

Errors are `{"error": {"message": ..., "type": ...}}` with real status codes: 400 for a bad request or an over-long prompt, 401 for a wrong key, 404 for an unknown model, 503 while loading, after a failed load, or when the queue is full.

**Slow but alive.** A streaming request gets its headers and an `: accepted` comment as soon as it is accepted, then a `: keepalive` comment every 15 s while it waits in the queue or while a layer pass runs, so a router can tell slow from dead. **One at a time.** Windlass runs one generation at a time; others wait in a FIFO queue and the log shows each one's position. A client that disconnects cancels its generation at the next layer, freeing the GPU for the next request.

## Register it in Fairlead

1. In Fairlead, **Providers → Add provider**, choose the **Custom (OpenAI-compatible)** preset.
2. Base URL `http://127.0.0.1:8777/v1` (or `http://<gpu-box>:8777/v1` with `--lan`). Add the API key if you set one.
3. Tick **Local**, and set the timeout to **3600** seconds.
4. Fetch models; Fairlead reads the id and context length from `/v1/models`.
5. **Routes → Add route** named `deep-local`, with the Windlass model as its only target, hedging off, and local fallback off. Do not add Windlass to the `local` route or the auto rules: it is far too slow for interactive traffic, so only requests that ask for `deep-local` by name should reach it.

## Test it

```bash
.venv/Scripts/python -m pytest
```

The API tests run a real server against a stub model and check the contract above: stream format, usage, error shapes and status codes, early headers, keepalives, queueing, the full-queue 503 and cancel on disconnect. The engine tests build tiny random Llama, Mistral, Qwen2 and Qwen3 models and check that layer-by-layer output matches the ordinary in-memory model exactly, with and without the KV cache, tied and untied embeddings, and with 8-bit and 4-bit shards.

The smoke test starts Windlass on [SmolLM2-135M-Instruct](https://huggingface.co/HuggingFaceTB/SmolLM2-135M-Instruct) (a ~270 MB Llama-family model) with no console window, streams a reply through `/v1/chat/completions` and checks `[DONE]` and the usage chunk:

```bash
.venv/Scripts/python scripts/smoke.py
```

Add `--compression 4bit` to test compressed shards, or `--url http://127.0.0.1:8777/v1` to test a server that is already running.

## Licence

MIT, see [LICENSE](LICENSE). The layer-streaming approach is adapted from AirLLM (Apache-2.0); see [THIRD-PARTY.md](THIRD-PARTY.md).

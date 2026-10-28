# Using Windlass

## Commands

```bat
Launch-Windlass.bat serve --model D:\models\model.gguf                 :: the API on http://127.0.0.1:8777/v1
Launch-Windlass.bat info  --model D:\models\model.gguf                 :: shape, file sizes, RAM per layer, KV cache per token
Launch-Windlass.bat run   --model D:\models\model.gguf --prompt "Hi"   :: one reply in the console, no server
Launch-Windlass.bat check                                              :: locality checks and compile only
Launch-Windlass.bat help                                               :: every option
```

For a split model (`name-00001-of-00004.gguf`), give any one part. While the model loads, chat requests get
`503 model_loading`. Stop the server with **Ctrl+C** or `src\Stop-Windlass.ps1`.

## Options

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

## Models

GGUF files of the `llama` (Llama 2, Llama 3.x, Mistral 7B, Mistral Large and other Llama-layout models), `qwen2`
(Qwen2, Qwen2.5) and `qwen3` (dense Qwen3) architectures, in F32, F16, BF16, Q8_0, Q5_0, Q5_1, Q4_0, Q4_1, Q6_K,
Q5_K, Q4_K, Q3_K or Q2_K (so the usual `Q8_0`, `Q6_K`, `Q5_K_M`, `Q4_K_M`, `Q3_K_M` and `Q2_K` files).

Not yet: mixture-of-experts models, I-quants (`IQ*`), vision models, and other architectures (Gemma is Phoenix's
job; `qwen35` is not supported yet). Windlass says so and refuses them at load.

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

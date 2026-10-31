# Using Windlass

## Commands

```bat
Launch-Windlass.bat serve --model D:\models\model.gguf                 :: the API on http://127.0.0.1:8777/v1
Launch-Windlass.bat info  --model D:\models\model.gguf                 :: shape, file sizes, RAM per layer, KV cache per token
Launch-Windlass.bat run   --model D:\models\model.gguf --prompt "Hi"   :: one reply in the console, no server
Launch-Windlass.bat playbook probe --model D:\models\model.gguf        :: measure this computer, write its playbook
Launch-Windlass.bat playbook show  --model D:\models\model.gguf        :: the settings serve and run would use now, and why
Launch-Windlass.bat playbook eval  --model D:\models\model.gguf        :: quality score (perplexity) and prompt speed
Launch-Windlass.bat commands --json                                    :: every command, for scripts and agents
Launch-Windlass.bat check                                              :: locality checks and compile only
Launch-Windlass.bat help                                               :: every option
```

For a split model (`name-00001-of-00004.gguf`), give any one part. While the model loads, chat requests get
`503 model_loading`. Stop the server with **Ctrl+C** or `src\Stop-Windlass.ps1`.

## The machine playbook

Windlass sets itself up for the computer it runs on, whether that is a laptop on battery, a workstation or a server.
Each time `serve` or `run` starts, it reads what the computer has (free RAM right now, total RAM, CPU cores, whether
it runs on battery, graphics) and what the model needs (layer sizes, KV cache per token), and picks:

| Setting | Rule |
| --- | --- |
| RAM budget | Free RAM minus a reserve for Windows and other programs (15% of total, at least 4 GB) |
| Context | The model's, at most 8192, halved until its KV cache fits in a quarter of the budget (at least 2048) |
| Prefetch | On when two layer slots fit beside the KV cache, so reading the next layer overlaps computing this one |
| Pinned layers | The rest of the budget keeps layers in RAM, read once instead of for every token; the whole model when it fits |
| Threads | One per physical core on battery; otherwise the faster of physical and logical cores as measured, or all logical cores |
| Guess-ahead | For greedy replies (temperature 0): guesses the next words from text already in the conversation and checks up to 8 in one pass. On when that pass costs at most 2.5x a normal token (measured), or, unmeasured, when the model streams from disk; off when real runs kept fewer than 15% of guesses. The reply is the same either way |

`playbook probe` adds measurements: how fast this computer reads layers, and how long one layer's math takes on its
physical and on its logical cores. With them the playbook picks the thread count and predicts seconds per token, and
names the limit (the disk or the CPU). It also times a token plus 8 guesses in one pass, which decides guess-ahead.

The playbook also learns from real runs: every reply adds its prompt and reply tokens, the time each took, and how
many guesses were kept. `playbook show` reports them next to the prediction, and the next start uses them (for example,
guess-ahead turns itself off where guesses rarely come true). A new `playbook probe` starts the totals over. Playbooks are saved in `playbooks\<model>.json` and reused while the computer
and the model file stay the same; free RAM is checked again at every start. Every choice is logged with its reason,
`playbook show` prints them without starting anything, and any option you give yourself wins.

`playbook eval` scores the model's quality on this machine: perplexity on a built-in passage written for the test (so no model has memorised it) (lower is
better; compare quantizations of one model, or models that share a tokenizer) and the seconds per prompt token. The
score is kept in the playbook and shown by `playbook show`. How speed and quality are meant to trade off is in
[SPEED-QUALITY.md](SPEED-QUALITY.md).

`--json` on `playbook probe`, `playbook show`, `playbook eval` and `commands` prints a versioned JSON document (`windlass.playbook/1`,
`windlass.commands/1`), and `--out <file>` writes it as UTF-8 without a BOM, so scripts and agents can use them
(see the Fleet CLI parity proposal). Exit code 0 is success, 1 an error, 2 a usage error.

## Options

| Option | Default | What it does |
| --- | --- | --- |
| `--model` | (required) | The GGUF file. |
| `--model-id` | file name | The id published in `/v1/models`. |
| `--port` | 8777-8779 | A fixed port instead of the first free one in the block. |
| `--api-key` | `$env:WINDLASS_API_KEY` | Require `Authorization: Bearer <key>` (or `X-Api-Key`) on `/v1`. |
| `--context-length` | playbook (model's, at most 8192) | Context to allow. The KV cache grows with use, up to this. |
| `--cache-gb` | playbook (`0` with `--no-playbook`) | Keep the first layers that fit in this many GB resident, so they are read from disk once. |
| `--no-prefetch` | off | Do not read the next layer while the current one computes (saves one layer of RAM). |
| `--threads` | playbook | Worker threads for the math. |
| `--int-dot` | off | Integer dot products with 8-bit activations (llama.cpp's CPU method) for Q8_0, Q4_0, Q4_K, Q5_K and Q6_K. In principle faster per generated token on the CPU (unmeasured on real models); logits move slightly because activations are rounded to 8 bits. |
| `--guess`, `--no-guess` | playbook | Guess-ahead for greedy replies on or off. |
| `--no-playbook` | off | Ignore the playbook: the defaults below plus the options you give. |
| `--chat-template` | detected | `llama3`, `chatml`, `mistral`, `mistral-v7` or `llama2`. |
| `--max-tokens` | `1024` | `max_tokens` for requests that give none. |
| `--max-queue` | `8` | Requests that may wait behind the running one; more get `503 server_busy`. |
| `--keepalive` | `15` | Seconds between SSE `: keepalive` comments (under 60). |
| `--stub` | off | Serve a canned reply with no model, to test wiring. |
| `--verbose` | off | Log every layer. |

## Models

GGUF files of the `llama` (Llama 2, Llama 3.x, Mistral 7B, Mistral Large and other Llama-layout models), `qwen2`
(Qwen2, Qwen2.5) and `qwen3` (dense Qwen3) architectures, in F32, F16, BF16, Q8_0, Q5_0, Q5_1, Q4_0, Q4_1, Q6_K,
Q5_K, Q4_K, Q3_K or Q2_K (so the usual `Q8_0`, `Q6_K`, `Q5_K_M`, `Q4_K_M`, `Q3_K_M` and `Q2_K` files), or in the
I-quants IQ4_NL, IQ4_XS, IQ3_XXS, IQ3_S, IQ2_XXS, IQ2_XS, IQ2_S, IQ1_S or IQ1_M (the `IQ*` files).

The I-quant decoders are ports of llama.cpp's `dequantize_row_iq*` and are tested against an independent re-derivation
of the block layouts, and for exact agreement between the streamed, prefetched and resident paths on tiny models. They
are **not yet checked against llama.cpp**: there is no llama.cpp on the development machine. To verify, run one real
IQ model (for example an IQ4_XS or IQ3_S file) through Windlass and llama.cpp with the same prompt at temperature 0 and
compare the logits or the first tokens. I-quants have no fused dot kernel, so each token decodes a row and then dots it
(like Q2_K and Q3_K); their speed is unmeasured.

Not yet: mixture-of-experts models, vision models, and other architectures (Gemma is Phoenix's
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

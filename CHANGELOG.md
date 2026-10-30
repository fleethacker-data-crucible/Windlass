# Changelog

## Unreleased

- `GET /v1/models` and `GET /v1/models/{id}` carry a `windlass` extension object (`windlass.model/1`, additive; every OpenAI field is unchanged): predicted and observed seconds per token and per prompt token with `measured` and `source`, the limiting resource (disk or CPU), perplexity with its token count and when it was measured, the context-fidelity score, quant, architecture, layer count, file bytes, context length, whether guess-ahead is on and the queue length. Never-measured values are `null`. Read from the playbook without the engine lock; `Backend.Facts()` is the new hook (documented in `docs/API.md`).
- `playbook fidelity --model <file> [--facts N] [--tokens N] [--questions K] [--seed N]`: the context-fidelity check. A deterministic synthetic ship's log with N planted facts (names, numbers, codes invented for the test) is read as one prompt, then each fact is asked about (greedy, small `max_tokens`) and recalls are counted. Prints `windlass.fidelity/1` with `--json`; the score is saved in the playbook next to the perplexity and shown by `playbook show`. The default is small (4 facts, about 300 tokens); long budgets are slow on a CPU. A tiny random model scores about chance, so the tests prove the machinery, not model quality.

- `playbook eval`: quality score (perplexity on a built-in passage written for the test (so no model has memorised it)) and prompt seconds per token, kept in the playbook; `docs/SPEED-QUALITY.md` sets out free vs costly speed-ups and the planned speed/quality dial.
- Guess-ahead (prompt-lookup speculation) for greedy replies: up to 8 guesses from text already in the conversation, checked in one pass; the reply is unchanged. The playbook turns it on or off from a measured batch cost and from how many guesses real runs kept (`--guess` / `--no-guess` override).
- The playbook learns from real runs (tokens, seconds, guesses kept), reports them in `playbook show` and uses them at the next start.
- The KV cache is fp16 (llama.cpp's default), half the memory: 0.33 MB per token for Llama 3.3 70B, so the playbook can pin more of the model.
- Machine playbook: `serve` and `run` read the computer (free RAM, cores, battery) and pick the context, prefetch, how much of the model to pin in RAM, and the thread count, each with a logged reason. `playbook probe` measures read speed and layer time to choose threads and predict seconds per token; `playbook show` prints the settings without starting. Options given by hand win; `--no-playbook` turns it off.
- `--threads`, and `commands --json`; `playbook probe|show` take `--json` and `--out` (versioned JSON, UTF-8 without a BOM), following the Fleet CLI parity proposal.
- RoPE cos/sin tables are computed once per position instead of for every layer and token.
- Stress-test report for Llama 3.3 70B (`docs/STRESS-TEST.md`, charts in `docs/stress-test/`): phases 1–6 pass; the soak and kill-and-restart phases are still to run.
- `Launch-Windlass.bat` clears `PSModulePath`, and `Start-Windlass.ps1` also filters the Microsoft Store PowerShell 7 module path, so starting from PowerShell 7 works every time.
- `Windlass.bat` is now `Launch-Windlass.bat`, the Fleet launcher name.
- Starting Windlass from PowerShell 7 works: `Start-Windlass.ps1` drops PowerShell 7's module paths, which made `serve` fail with "Import-PowerShellDataFile is not recognized".
- Brand: placeholder emblem (light and dark), `mark.svg`, `mark-256.png` and `favicon.ico`, rendered by `build\New-WindlassBrand.ps1`.
- README follows the Fleet README standard: theme-aware emblem, console screenshots from a real 70B run, no badges, and the reference sections moved to `docs/` (USAGE, API, ARCHITECTURE, DEVELOPMENT).
- First measured speeds on real models (Llama 3.2 3B and Llama 3.3 70B) in `docs/ARCHITECTURE.md`.

## 0.2.0 (2026-10-26)

Rewritten in C# 5 on Windows PowerShell 5.1, the Fleet workstation stack. No Python, PyTorch or other installs.

- Reads GGUF models (single or split files) instead of Hugging Face checkpoints; no split step and no shard folder. Weights stay in their GGUF quantization (F32, F16, BF16, Q8_0, Q5_0, Q5_1, Q4_0, Q4_1, Q6_K, Q5_K, Q4_K, Q3_K, Q2_K), so `--compression` is gone.
- Llama 2/3, Mistral, Qwen2/2.5 and Qwen3 dense models, with built-in tokenizers and chat templates.
- Layer streaming with two RAM slots and prefetch; `--cache-gb` keeps the first layers resident. CPU compute only for now.
- The KV cache is kept between requests and a shared prompt prefix is reused.
- Same OpenAI-compatible API contract as v0.1 (streaming, usage, keepalives, FIFO queue, cancel on disconnect), plus `/__fleethacker/id` and `/__fleethacker/shutdown`, `Host`/`Origin` checks and loopback-only binding. `--lan`, `--host`, `--device`, `--dtype`, `--hf-token` and `--allow-untested-arch` are gone.
- Port block 8777-8779 (policy `exclusive`) with `src\Stop-Windlass.ps1`; `Windlass.bat` starts it.
- Tests in C# with a Pester 5 wrapper, checked against llama.cpp reference outputs.

## 0.1.0 (2026-10-25)

First cut in Python (PyTorch + Transformers), refactored from AirLLM. Retired in 0.2.0.

# Changelog

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

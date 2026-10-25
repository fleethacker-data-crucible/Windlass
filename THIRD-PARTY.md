# Third-party notices

## AirLLM

- Project: [AirLLM](https://github.com/lyogavin/airllm) by Gavin Li and contributors
- Licence: Apache License 2.0 (full text in [LICENSES/AirLLM-Apache-2.0.txt](LICENSES/AirLLM-Apache-2.0.txt))
- Reviewed at commit `c2ac5944b3a6b13050b021dd64d4462cecf2bcb7` (2026-10-25)

Windlass is a clean refactor of AirLLM's technique, not a copy of its code. The ideas taken from AirLLM are:

- splitting a checkpoint into one safetensors file per layer (embeddings, each decoder layer, final norm, output head);
- building the Transformers model on the `meta` device and attaching forward hooks that load each layer's weights just before it runs and free them just after;
- prefetching the next layer on a worker thread while the current one computes, with a size cap on pinned host memory;
- keeping a tied embedding resident instead of streaming it;
- compressing layer shards to 8-bit or 4-bit to cut disk reads.

The files that implement these ideas, and so are modified works of AirLLM under section 4 of the Apache License, are:

- `windlass/engine/shards.py`
- `windlass/engine/layered.py`
- `windlass/engine/quant.py` (re-implemented in plain PyTorch instead of bitsandbytes)

Everything else (the HTTP server, the scheduler, the generation loop, the tests and the scripts) is original to Windlass. Windlass as a whole is licensed under MIT; the files above remain subject to the Apache License 2.0 as well.

AirLLM ships no NOTICE file, so there is no NOTICE text to carry over.

## NF4 code values

The 16 NF4 code values in `windlass/engine/quant.py` come from the QLoRA paper (Dettmers et al., 2023, "QLoRA: Efficient Finetuning of Quantized LLMs"), as published in bitsandbytes (MIT).

## Runtime dependencies

Windlass installs, but does not vendor, PyTorch (BSD-3-Clause), Hugging Face Transformers, Accelerate, safetensors and huggingface_hub (Apache-2.0), FastAPI (MIT), Uvicorn (BSD-3-Clause) and, for tests, pytest (MIT) and HTTPX (BSD-3-Clause).

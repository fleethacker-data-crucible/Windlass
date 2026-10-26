# Third-party notices

Windlass ships no third-party code at run time: no packages, no DLLs. The sources below are ported or adapted into its own C#.

## llama.cpp / ggml

- Project: [llama.cpp](https://github.com/ggml-org/llama.cpp), Copyright (c) 2023-2026 The ggml authors
- Licence: MIT (full text in [LICENSES/llama.cpp-MIT.txt](LICENSES/llama.cpp-MIT.txt))

Ported from it: the GGUF format reader (`src/Gguf.cs`, after `gguf.md`), the weight decoders (`src/Quants.cs`, after the `dequantize_row_*` reference functions in `ggml-quants.c`; the fused dot kernels follow the same layouts), the tokenizers (`src/Tokenizer.cs`, after `llm_tokenizer_bpe` and `llm_tokenizer_spm` in `llama-vocab.cpp`, including the pre-tokenizer regexes), the model hyperparameter keys (`src/ModelConfig.cs`), the forward pass layout (`src/Transformer.cs`, after `llm_build_llama`, `llm_build_qwen2` and `llm_build_qwen3`) and the built-in chat templates (`src/ChatTemplate.cs`). The test fixture `tests/fixtures/llama-cpp-reference.txt` holds outputs computed with llama.cpp (through llama-cpp-python 0.3.36 on a development machine).

## Phoenix LAI

- Project: [Phoenix LAI](https://github.com/fleethacker-data-crucible/phoenix-lai), Copyright (c) 2026 Jeremy Gerdes and contributors
- Licence: MIT

Taken from it: `src/Half.cs` and `src/Json.cs` (unchanged apart from the namespace and integer output), and the byte-level BPE part of `src/Tokenizer.cs`.

## FleetHacker edge-ops

- `src/FleetPort.psm1` is a copy of [tools/fleet-port/FleetPort.psm1](https://github.com/fleethacker-data-crucible/edge-ops/tree/main/tools/fleet-port) (MIT), the suite's port-block and self-detection helper. `PSScriptAnalyzerSettings.psd1` and `.gitattributes` come from edge-ops too.

## AirLLM

- Project: [AirLLM](https://github.com/lyogavin/airllm) by Gavin Li and contributors, Apache License 2.0

Windlass takes the idea of layer-by-layer inference from AirLLM: keep one layer in memory, load each layer just before it runs and free it after, prefetch the next layer while the current one computes, and keep the KV cache across layers. Windlass v0.1 was a Python refactor of AirLLM's code; v0.2 is a new C# implementation over GGUF files and contains no AirLLM code.

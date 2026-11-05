# Third-party notices

Windlass ships no third-party code at run time: no packages, no DLLs. The sources below are ported or adapted into its own C#; the browser UI's bundled packages are listed at the end of the C# sources.

## llama.cpp / ggml

- Project: [llama.cpp](https://github.com/ggml-org/llama.cpp), Copyright (c) 2023-2026 The ggml authors
- Licence: MIT (full text in [LICENSES/llama.cpp-MIT.txt](LICENSES/llama.cpp-MIT.txt))

Ported from it: the GGUF format reader (`src/Gguf.cs`, after `gguf.md`), the weight decoders (`src/Quants.cs`, after the `dequantize_row_*` reference functions in `ggml-quants.c`; the fused dot kernels follow the same layouts), the I-quant decoders (`src/IQuants.cs`, after `dequantize_row_iq*` in `ggml-quants.c`) and their lookup tables (`src/IqTables.cs`, the `iq*_grid`, `ksigns_iq2xs`, `kmask_iq2xs` and `kvalues_iq4nl` tables of `ggml-common.h`), the tokenizers (`src/Tokenizer.cs`, after `llm_tokenizer_bpe` and `llm_tokenizer_spm` in `llama-vocab.cpp`, including the pre-tokenizer regexes), the model hyperparameter keys (`src/ModelConfig.cs`), the forward pass layout (`src/Transformer.cs`, after `llm_build_llama`, `llm_build_qwen2` and `llm_build_qwen3`) and the built-in chat templates (`src/ChatTemplate.cs`). The test fixture `tests/fixtures/llama-cpp-reference.txt` holds outputs computed with llama.cpp (through llama-cpp-python 0.3.36 on a development machine).

## Phoenix LAI

- Project: [Phoenix LAI](https://github.com/fleethacker-data-crucible/phoenix-lai), Copyright (c) 2026 Jeremy Gerdes and contributors
- Licence: MIT

Taken from it: `src/Half.cs` and `src/Json.cs` (unchanged apart from the namespace and integer output), and the byte-level BPE part of `src/Tokenizer.cs`. The IQ2_XXS, IQ2_S and IQ3_S grids in `src/IqTables.cs` come from Phoenix's `src/IqTables.cs`; the IQ2_XS, IQ3_XXS and IQ1_S grids were read from the ggml data of a compiled llama.cpp (the `ggml-base.dll` that ships with Ollama) and agree with it byte for byte where the two overlap.

## FleetHacker edge-ops

- `src/FleetPort.psm1` is a copy of [tools/fleet-port/FleetPort.psm1](https://github.com/fleethacker-data-crucible/edge-ops/tree/main/tools/fleet-port) (MIT), the suite's port-block and self-detection helper. `PSScriptAnalyzerSettings.psd1` and `.gitattributes` come from edge-ops too.

## Browser UI (compiled into `ui\dist`)

The UI is built off the workstation from `ui-src`; the compiled files contain these MIT or ISC packages, bundled (nothing is loaded at run time): [Svelte](https://github.com/sveltejs/svelte) (MIT), [Tailwind CSS](https://github.com/tailwindlabs/tailwindcss) (MIT), [Lucide](https://github.com/lucide-icons/lucide) icons via `@lucide/svelte` (ISC), [clsx](https://github.com/lukeed/clsx) (MIT) and [tailwind-merge](https://github.com/dcastil/tailwind-merge) (MIT). Vite, Vitest, TypeScript and svelte-check are build and test tools and are not shipped. `ui-src/vendor/fleet/fleet.css` and `docs/UNIFIED-UI.md` are the Fleet's shared files, identical in every Fleet repo.

## AirLLM

- Project: [AirLLM](https://github.com/lyogavin/airllm) by Gavin Li and contributors
- Licence: Apache License 2.0 (full text in [LICENSES/AirLLM-Apache-2.0.txt](LICENSES/AirLLM-Apache-2.0.txt))

Windlass is derived from and refactored from AirLLM. v0.1 was a Python refactor of AirLLM's layer-streaming design: keep one layer in memory, load each layer just before it runs and free it after, prefetch the next layer while the current one computes, and keep the KV cache across layers. v0.2 is a C# rewrite of that design over GGUF files. The current v0.2 tree ships no AirLLM source.

AirLLM publishes no NOTICE file. This section is the notice for the derived work: AirLLM (https://github.com/lyogavin/airllm), Copyright 2023 Gavin Li and contributors, under the Apache License, Version 2.0.

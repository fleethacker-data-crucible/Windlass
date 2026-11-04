# Developing Windlass

## Tests

```bat
powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests\Run-Tests.ps1
```

`Run-Tests.ps1` compiles `src` and `tests` in memory and runs the C# suite (`tests\Tests.cs`);
`tests\Windlass.Tests.ps1` wraps it for Pester 5, one Pester test per C# test. No model download is needed:

- **Engine.** `tests\TinyModels.cs` writes tiny random GGUF models (Llama, Qwen2 with biases, Qwen3 with QK norm,
  tied embeddings, Llama 3.1 `rope_freqs`, a SentencePiece vocabulary, a split file) in every supported weight type.
  Their logits are compared with llama.cpp's for the same files (`tests\fixtures\llama-cpp-reference.txt`). Streamed,
  prefetched and resident runs must give identical logits, token-by-token decoding must match a full pass, and the
  fused kernels must match decode-then-dot.
- **Mixture of experts.** `tests\TinyMoe.cs` writes tiny MoE models in the llama/mixtral, qwen2moe and qwen3moe
  layouts; `tests\MoeTests.cs` checks them against a naive double-precision reference written in the test (decode,
  route, mix), the one-expert case against the dense model, top-k and renormalisation, that only chosen experts
  compute, and streaming and KV-cache exactness. None of it compares with llama.cpp.
- **Granite and OLMo 2.** `tests\TinyArchs.cs` and `tests\ArchTests.cs`: a reference match for the scales (Granite)
  and the post-norms and full-vector QK norm (OLMo 2), a Granite file with no scale keys equal to the Llama model,
  and streaming and KV-cache exactness. Not compared with llama.cpp.
- **Tokenizer.** Token ids match llama.cpp's for Llama 3 BPE, Qwen2 BPE and SentencePiece vocabularies, including
  special tokens, Unicode and whitespace runs.
- **API.** A real server on loopback port 18777 (the suite's CI range) against a stub model checks the contract in
  [API.md](API.md): stream format, usage, error shapes and status codes, the API key, 503 while loading, early
  headers, keepalives while queued, the full-queue 503, cancel on disconnect, Host and Origin checks, and one
  end-to-end request through a real tiny model.

## The UI

`ui-src\` is the source (Svelte 5, Tailwind 4); its compiled output `ui\dist\` is committed and served by `src\Ui.cs`. Build,
check and test it off the workstation ([UI.md](UI.md#building-it)). `tests\UiTests.cs` covers the server side with no
browser: the files and headers, exact-match serving, the session token and API key, the origin rule, the logs cursor and the
rule that only `serve --open` belongs to its tab. Change the page, run `npm run build` and commit `ui\dist` with it.

## Checks and lint

`Launch-Windlass.bat check` runs the locality checks and compiles the C# without starting anything. Lint with
PSScriptAnalyzer and `PSScriptAnalyzerSettings.psd1`. All C# is C# 5 (the .NET Framework 4.8 CodeDom compiler) and
ASCII.

## Brand assets

`powershell -File build\New-WindlassBrand.ps1` (needs Edge or Chrome) renders `assets\brand\mark.svg` to
`mark-256.png` and `favicon.ico` (the emblems are final artwork now; a placeholder is rendered only if `assets\brand\emblem-source.html`
exists). The README screenshots are rendered from `docs\screenshots\console-source.html` (instructions
in that file).

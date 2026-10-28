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
- **Tokenizer.** Token ids match llama.cpp's for Llama 3 BPE, Qwen2 BPE and SentencePiece vocabularies, including
  special tokens, Unicode and whitespace runs.
- **API.** A real server on loopback port 18777 (the suite's CI range) against a stub model checks the contract in
  [API.md](API.md): stream format, usage, error shapes and status codes, the API key, 503 while loading, early
  headers, keepalives while queued, the full-queue 503, cancel on disconnect, Host and Origin checks, and one
  end-to-end request through a real tiny model.

## Checks and lint

`Launch-Windlass.bat check` runs the locality checks and compiles the C# without starting anything. Lint with
PSScriptAnalyzer and `PSScriptAnalyzerSettings.psd1`. All C# is C# 5 (the .NET Framework 4.8 CodeDom compiler) and
ASCII.

## Brand assets

`powershell -File build\New-WindlassBrand.ps1` (needs Edge or Chrome) renders `assets\brand\mark.svg` to
`mark-256.png` and `favicon.ico`, and the placeholder emblem `assets\brand\emblem-source.html` to `emblem-light.png`
and `emblem-dark.png`. The README screenshots are rendered from `docs\screenshots\console-source.html` (instructions
in that file).

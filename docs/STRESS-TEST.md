# Windlass stress test, 27–28 October 2026

Llama 3.3 70B Instruct (Q4_K_M, 42.5 GB) served by Windlass 0.2 on the CPU, reached directly and through Fairlead's `deep-local` route. The phases get harder as they go: errors, cancelling, a full queue, a long answer, a long prompt, a conversation that grows toward the 8k context, an hour of back-to-back requests, and finally killing Windlass.

**Result: phases 1–6 passed on Windlass; phases 7 (one-hour soak) and 8 (kill and restart) are paused and will be added.** Three phases are yellow because of bugs in Fairlead, the router in front of Windlass, not in Windlass; [why, and how they turn green](#why-three-phases-are-yellow-pass).

<p align="center"><img src="stress-test/scorecard.svg" alt="Scorecard of the eight phases: error handling, full queue and long prompt pass on Windlass but are yellow because of Fairlead bugs; cancel, long answer and long conversation (12 of 12 recalls) pass; the soak and kill-and-restart are not run yet" width="100%"></p>

## Why three phases are yellow (PASS*)

Each phase checks two things: what **Windlass** does, and what a client sees when the same request goes through **Fairlead**, the router that sits in front of Windlass on the `deep-local` route. In phases 1, 3 and 5 Windlass did exactly the right thing, so the phase passes. Fairlead then passed the result on wrongly, so the card is marked PASS* (yellow) instead of green. These are bugs in Fairlead, not in Windlass, and they are being fixed there. Once each fix is in, the phase is rerun through Fairlead and its card turns green.

| Phase | Windlass did (correct) | Fairlead passed on | It should pass on | Turns green when |
|---|---|---|---|---|
| 1 · Error handling | Prompt too long for the 8,192-token context: `400 context_length_exceeded`, "The prompt is 13209 tokens; this server's context is 8192 tokens." | `503` "Route 'deep-local' has no target that can take this request. Add one in Fairlead's Routes page." It checked the prompt against the model's context size itself and then blamed the route. | `400 context_length_exceeded` with the size in the message, so the client knows to shorten the prompt | Fairlead returns 400 for a prompt over every target's context |
| 3 · Full queue | Queue of 8 full: `503 server_busy` with `Retry-After: 60` | `502` "windlass answered HTTP 503: …" | `503` with the `Retry-After` header, so clients wait and retry instead of treating it as a broken server | Fairlead passes a provider's 503 and `Retry-After` through on the last target |
| 5 · Long prompt | Read a 2,927-token prompt in 60 min while sending a keepalive every 15 s, then answered correctly | Cut the request at exactly 3,600 s, just as the answer started, with "stopped sending (OperationCanceledException)" | Keep waiting while keepalives arrive (the timeout should be a silence limit, as Fairlead's Windlass notes say), and name its own timeout in the error | Fairlead's provider timeout resets on each keepalive, and the 2,927-token prompt completes through `deep-local` |

Fairlead also drops the keepalives instead of forwarding them to its clients (finding 3 below). That doesn't change a card, but fixing it with phase 5 lets clients see that a slow request is still alive.

**Machine:** Ryzen 9 9950X3D (16 cores, 32 threads), 96 GB RAM, Samsung 9100 PRO PCIe 5.0 NVMe, Windows 11 Pro. GPU not used. **Model:** `Llama-3.3-70B-Instruct-Q4_K_M.gguf` (bartowski, SHA-256 `32df3bac…`). **Runs:** run 1 on 27 October 20:22–23:31 (phases 1–4, phase 5 through Fairlead, phase 6 turns 1–5), stopped when another session shut the PC down; run 2 on 28 October 02:43–06:49 (phase 5 again, phase 6 from the start, the first minutes of phase 7). Phase 5 was rerun directly against Windlass, because in run 1 Fairlead's timeout ended it.

## Phases

| # | Phase | What happened | Result |
|---|---|---|---|
| 1 | Error handling | Unknown model: `404`. Prompt over the context, direct: `400` "The prompt is 13209 tokens; this server's context is 8192 tokens." Same prompt through Fairlead: `503` "Route 'deep-local' has no target that can take this request." | Windlass ✅ · Fairlead ⚠️ |
| 2 | Cancel mid-answer | Client hung up through Fairlead after the first token (29 s); Windlass freed its turn 22 s later. | ✅ |
| 3 | Full queue | 1 running + 8 queued. 10th request direct: `503` server_busy. 11th through Fairlead: `502` "windlass answered HTTP 503…". Queue empty 30 s after the clients hung up. | Windlass ✅ · Fairlead ⚠️ |
| 4 | Long answer | 300 tokens in 43.1 min, first word at 76 s, 8.4 s per token. | ✅ |
| 5 | Long prompt | 2,927-token ship's log, question about a fact in the middle. First word at 60.0 min, answer correct: "On Day 7, the ship called at Brightwater, where Greta Lund signed on as watch officer." 239 keepalives reached the client. Through Fairlead (run 1) the same request was cut at 3,600 s. | Windlass ✅ · Fairlead ⚠️ |
| 6 | Long conversation | 12 turns, each adding ~550 tokens of log and asking about an earlier day. Context grew from 521 to 6,576 tokens; first word stayed at 10.2–12.3 min. Recalls correct: **12 of 12**. | ✅ |
| 7 | One-hour soak | Started 28 October 06:37 and paused at 06:49 for the day; to be finished in a later run. | ⏸ |
| 8 | Kill and restart | Not run yet (runs after phase 7). | ⏸ |

## Long conversation

<p align="center"><img src="stress-test/conversation.svg" alt="Bar chart of conversation size per turn growing from about 500 to 6,600 tokens, with the time to the first word nearly flat between 10 and 12.5 minutes; all 12 recalls correct" width="100%"></p>

| Turn | Context (tokens) | First word | Total | s / token | Recall |
|---|---|---|---|---|---|
| 1 | 521 | 614 s | 795 s | 7.54 | ✅ 550 metres, Brightwater |
| 2 | 1,069 | 637 s | 821 s | 7.67 | ✅ 560 metres, Fenmouth |
| 3 | 1,619 | 635 s | 819 s | 7.66 | ✅ 550 metres, Brightwater |
| 4 | 2,171 | 637 s | 819 s | 7.58 | ✅ 570 metres, Ravensreach |
| 5 | 2,725 | 648 s | 832 s | 7.7 | ✅ 550 metres, Brightwater |
| 6 | 3,274 | 650 s | 851 s | 7.73 | ✅ 580 metres, Port Kestrel |
| 7 | 3,823 | 656 s | 842 s | 7.74 | ✅ 550 metres, Brightwater |
| 8 | 4,371 | 672 s | 858 s | 7.72 | ✅ 590 metres, Graywick |
| 9 | 4,921 | 682 s | 868 s | 7.78 | ✅ 550 metres, Brightwater |
| 10 | 5,473 | 704 s | 892 s | 7.85 | ✅ 600 metres, Lantern Point |
| 11 | 6,027 | 734 s | 927 s | 8.04 | ✅ 550 metres, Brightwater |
| 12 | 6,576 | 741 s | 935 s | 8.09 | ✅ 610 metres, Brightwater |

Each turn only processes its own new text: Windlass keeps the conversation's KV cache and skips the prefix it has already seen. The small rise in time per token (7.5 → 8.1 s) is attention over the longer conversation.

## The machine

<p align="center"><img src="stress-test/machine.svg" alt="Line chart of Windlass memory and CPU clock during run 2: memory moves between a few GB and about 24 GB, with higher peaks late in the long conversation; the CPU clock holds flat at 4.3 GHz" width="100%"></p>

- **CPU clock:** 351 one-minute samples across both runs; 0 below 95 % of the 4.3 GHz base clock. No heat throttling, even at 100 % load for hours.
- **Windlass memory (working set):** between 0.5 and 23.6 GB. It rises and falls with each layer pass and returns to a few GB between requests. The peaks grew in the last turns of phase 6 and at the start of the soak, as the conversation's KV cache and prompt batches grew; the full soak will show whether they level off. They are well above the README's "two layers plus KV cache" estimate, most likely because Windows counts recently read model pages in the working set; worth a closer look.
- **CPU temperature:** not logged (Windows exposes no sensor without a driver). The motherboard showed about 79 °C under load with the fans corrected.

## Findings

Windlass did what its contract says in every phase. Findings 1–5 are in Fairlead, which sits in front of it, and are what keep phases 1, 3 and 5 yellow; they'll be fixed in Fairlead and those phases rerun:

1. **Fairlead's timeout ends the whole request at 3,600 s, even while Windlass sends keepalives.** A 2,927-token prompt needs about 60 minutes of prompt processing on this CPU, so through `deep-local` it can never finish. Fairlead's own Windlass integration notes say it only cuts streams that stay silent; for slow local providers the timeout should reset on each keepalive.
2. **Fairlead reports its own timeout as `windlass/llama-3.3-70b stopped sending (OperationCanceledException)`,** which reads like a crash in Windlass.
3. **Fairlead does not pass SSE keepalive comments on to its clients.** During the 40–60 s (or up to an hour) before the first word, a client through Fairlead receives nothing, so a client with a short read timeout gives up.
4. **A prompt over the context comes back from Fairlead as `503 "Route 'deep-local' has no target that can take this request. Add one in Fairlead's Routes page."`** It should be `400 context_length_exceeded`, as Windlass returns directly.
5. **A full Windlass queue comes back from Fairlead as `502`.** Windlass answered `503 server_busy` with `Retry-After`; passing that through would let clients retry.

And in Windlass:

6. **Starting `serve` from PowerShell 7 failed once in run 2** with "Import-PowerShellDataFile is not recognized", despite the fix in `Start-Windlass.ps1`. This PR also clears `PSModulePath` in `Launch-Windlass.bat` and filters the Microsoft Store PowerShell 7 module path, and both were checked from PowerShell 7.
7. **Cancelling a request that is still queued takes up to one keepalive interval (15–30 s)** to notice. That's fine, but not instant.

## What it means for use

- **Speed:** about 7.5–8.5 s per word-piece (token) for the 70B on this CPU, and about 1.2 s per token to read a prompt. A short answer to a short question takes 5–10 minutes; a 3,000-token document takes an hour before the first word.
- **Reliability so far:** no errors, crashes, leaks or throttling across about seven hours of load, and correct answers in every phase that checked them. The soak and the hard-kill recovery are still to run.
- **Next:** GPU offload (Windlass's roadmap) mainly speeds up prompt reading, the slow part here. In Fairlead, fixing findings 1 and 3 makes `deep-local` usable for long documents.

## Method

The test script sent OpenAI-style streaming requests (temperature 0) and logged every request and a one-minute sample of Windlass's memory, its CPU use and the CPU clock. Facts to recall came from a generated ship's log, so each answer could be checked automatically. A three-turn chat run before the stress test (Sea Biscuit, a 32-foot sloop) also passed: the model recalled the boat's name and length two turns later.

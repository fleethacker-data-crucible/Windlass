# Speed and quality

**Status: design, part built.** Windlass measures speed (`playbook probe`) and quality (`playbook eval`) on the
machine it runs on. The dial that trades one for the other is planned for Fairlead and described here.

## Two kinds of speed-up

| Kind | Examples | Effect on answers | Who decides |
| --- | --- | --- | --- |
| **Free** | Pinning layers in RAM, fp16 KV cache, prefetch, threads, guess-ahead, a GPU drafter checked by the big model, faster kernels | **None.** The model computes the same thing faster; guesses are always checked by the big model, so a greedy reply is unchanged | The machine playbook, automatically, wherever they help |
| **Costly** | A smaller quantization (Q2 instead of Q4), a smaller model, summarising long context before the big model reads it, routing easy questions to a small model | **Some.** Answers can be less accurate, or miss details | The person or program asking, through one dial |

Rule: free speed-ups are always on where they pay; costly ones happen only when someone turns the dial toward speed.

## Measuring both sides on this machine

- **Speed** (`playbook probe`): read speed, one layer's math on physical and logical cores, a token plus eight guesses
  in one pass. The playbook predicts seconds per token and names the limit (disk or CPU).
- **Quality** (`playbook eval`): perplexity on a fixed built-in passage written for the test, so no model has memorised it (a famous text such as Moby-Dick scored near-perfect on every model and could not tell them apart), and
  the seconds per prompt token from the same pass. Perplexity is how surprised the model is by each next word: lower
  is better. It compares quantizations of one model, and models that share a tokenizer (Llama 3.2 3B and Llama 3.3
  70B do). It does not capture everything a person means by quality (reasoning, following instructions), so treat it
  as a floor check: a large jump means real damage.
- **Context fidelity**, planned: a short version of the stress test's long-conversation phase (facts planted in a
  log, recalled later), scored as recalls correct. This is the check that would catch context compression losing
  details.

Each candidate (model file and settings) then has a measured speed and a measured quality on this machine.

## The dial (planned, in Fairlead)

One setting with three positions, per server or per request (for example a header `X-Fairlead-Priority:
quality | balanced | speed`, or a default on a client key):

| Position | Picks |
| --- | --- |
| `quality` | The best-scoring candidate, with every free speed-up. Never a costly one |
| `balanced` | The fastest candidate whose quality is within a set margin of the best (for example perplexity within 5%) |
| `speed` | The fastest candidate that still passes a minimum quality floor |

Fairlead already chooses between models (Ollama on the GPU, Windlass on the CPU), so it is the natural place for the
dial. Windlass's part is to publish its measured numbers, for example in `GET /v1/models`, so Fairlead can choose
without guessing. `deep-local` always stays on the big model, so a person can always get the slow, careful answer.

## A small model on the GPU

Three ways a tiny always-on GPU model could help, from safest to riskiest:

1. **Drafter (free):** the tiny model guesses the next several words; Windlass checks them all in one pass and keeps
   the ones the big model agrees with. Same answer, faster when guesses are right. Needs a cheaper batch pass in
   Windlass first (on the dev desktop a pass of nine tokens currently costs 7.3 single tokens), and a "check these
   guesses" API so Fairlead can pass drafts from Ollama to Windlass.
2. **Triage (costly, small risk):** the tiny model judges whether a question needs the big model. Only route away
   when it is very sure; `deep-local` always reaches the big model.
3. **Context compression (costly, real risk):** the tiny model shortens a long document before the big model reads
   it. Cuts prompt time sharply, but can drop details. Opt-in only, and only with the context-fidelity check above.

## Order of work

1. Done: free speed-ups in the playbook (pinning, fp16 KV, threads, guess-ahead with a measured on/off).
2. Done: `playbook eval` (perplexity and prompt speed).
3. Next in Windlass: a faster batch path (cheaper guess checking and much faster prompts), then the context-fidelity
   check, then publishing speed and quality in `/v1/models`.
4. In Fairlead: the dial, then the GPU drafter through a "check these guesses" API.

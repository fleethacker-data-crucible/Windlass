# Speed and quality

**Status: built in Windlass, the dial still planned.** Windlass measures speed (`playbook probe`), quality
(`playbook eval`) and context fidelity (`playbook fidelity`) on the machine it runs on, and publishes them in
`GET /v1/models` ([API.md](API.md#measured-numbers-in-v1models)). The dial that trades one for the other is planned for
Fairlead and described here.

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
- **Context fidelity** (`playbook fidelity`): a short version of the stress test's long-conversation phase (phase 6:
  facts in a ship's log, asked about later), scored as recalls correct. Windlass builds a synthetic log (original text,
  every name, number and code invented; deterministic from a seed and sized to a token budget) with N planted facts,
  then asks one question per fact through the normal chat path, greedy with at most 24 reply tokens. A recall counts when
  the reply contains the planted value as whole words (case, punctuation and thousands separators ignored; a first
  name alone does not recall a full name). The score, `correct of total`, is kept in the playbook with the log's token
  count and the check's version. This is the check that would catch context compression losing details.
  - **Small by default** (4 facts, about 300 tokens of prompt) because a prompt costs real time on a CPU; a long
    budget (`--tokens 3000`) is slow (about an hour on a 70B model at the stress test's 1.2 s per prompt token) and is
    for the owner of the machine to run when it can be spared. A short log says little about a long conversation:
    compare scores only at the same facts and token budget.
  - **Not parity-checked against anything.** The check is new; no other tool computes this score. A tiny random model
    recalls nothing (chance for free recall is close to zero), so the tests prove the machinery (scoring, the
    generator, recording, publishing), not the quality of any model. Unmeasured: how scores of real models compare, and
    how they move with log length.

Each candidate (model file and settings) then has a measured speed, quality and fidelity on this machine, and
`GET /v1/models` carries them (`null` where never measured), so Fairlead can choose without guessing.

## The dial (planned, in Fairlead)

One setting with three positions, per server or per request (for example a header `X-Fairlead-Priority:
quality | balanced | speed`, or a default on a client key):

| Position | Picks |
| --- | --- |
| `quality` | The best-scoring candidate, with every free speed-up. Never a costly one |
| `balanced` | The fastest candidate whose quality is within a set margin of the best (for example perplexity within 5%) |
| `speed` | The fastest candidate that still passes a minimum quality floor |

Fairlead already chooses between models (Ollama on the GPU, Windlass on the CPU), so it is the natural place for the
dial, and the dial itself is still planned (nothing in Windlass or Fairlead implements it yet). Windlass's part is built:
it publishes its measured numbers in `GET /v1/models` (seconds per token and per prompt token, predicted and observed,
the limiting resource, perplexity, context fidelity), so Fairlead can choose without guessing. `deep-local` always stays on the big model, so a person can always get the slow, careful answer.

## A small model on the GPU

Three ways a tiny always-on GPU model could help, from safest to riskiest:

1. **Drafter (free):** the tiny model guesses the next several words; Windlass checks them all in one pass and keeps
   the ones the big model agrees with. Same answer, faster when guesses are right. Needs a cheaper batch pass in
   Windlass first (on the dev desktop a pass of nine tokens currently costs 7.3 single tokens), and a "check these
   guesses" API so Fairlead can pass drafts from Ollama to Windlass.
2. **Triage (costly, small risk):** the tiny model judges whether a question needs the big model. Only route away
   when it is very sure; `deep-local` always reaches the big model.
3. **Context compression (costly, real risk):** the tiny model shortens a long document before the big model reads
   it. Cuts prompt time sharply, but can drop details. Opt-in only, and only with the context-fidelity check above
   (which exists; the compression does not).

## Order of work

1. Done: free speed-ups in the playbook (pinning, fp16 KV, threads, guess-ahead with a measured on/off).
2. Done: `playbook eval` (perplexity and prompt speed).
3. Done: the context-fidelity check (`playbook fidelity`), and speed, quality and fidelity published in `/v1/models`.
4. Next in Windlass: a faster batch path (cheaper guess checking and much faster prompts).
5. In Fairlead: the dial, then the GPU drafter through a "check these guesses" API.

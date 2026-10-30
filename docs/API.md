# Windlass API

Windlass serves an OpenAI-compatible API on **`http://127.0.0.1:8777/v1`** (the first free port in its block,
8777-8779). It binds loopback only.

## Endpoints

- **`POST /v1/chat/completions`**: OpenAI Chat Completions, `stream: false` or `true`. Streams are SSE
  `data: {chat.completion.chunk}` lines ending with `data: [DONE]`. `stream_options.include_usage` adds a final chunk
  with `usage`; non-stream replies always carry `usage`. Honours `max_tokens` / `max_completion_tokens`,
  `temperature` (0 = greedy), `top_p`, `stop` and `seed`. Message `content` may be a string or an array of `text`
  parts. `tools` are ignored (logged); Windlass does not do tool calling.
- **`GET /v1/models`** and **`GET /v1/models/{id}`**: `{"object": "list", "data": [{"id": ..., "context_length": N, ...}]}`,
  with a `windlass` object of measured numbers (below).
- **`GET /health`**: `status` (`loading`, `ready`, `error`), `busy`, `queued`.
- **`GET /__fleethacker/id`** and **`POST /__fleethacker/shutdown`**: the suite's self-detection endpoints.

## Measured numbers in `/v1/models`

The model card keeps every OpenAI field (`id`, `object`, `created`, `owned_by`) and Windlass's `context_length` and
`status` unchanged, and adds one object, `windlass` (schema `windlass.model/1`), so a router such as Fairlead can choose
between models without guessing. The keys are always all there. **A value that was never measured is `null`, never an
estimate.** It is read from the machine playbook (`playbook probe`, `playbook eval`, `playbook fidelity`, and real
runs) and never waits for a running generation, so it answers while a reply is being produced.

```json
{"id": "llama-3.3-70b", "object": "model", "created": 0, "owned_by": "windlass", "context_length": 8192, "status": "ready",
 "windlass": {
   "schema": "windlass.model/1",
   "architecture": "llama", "quant": "Q4_K", "layers": 80, "fileBytes": 42520395200,
   "contextLength": 8192, "modelContextLength": 131072, "guessAhead": true,
   "speed": {
     "secPerToken":       {"value": 8.1, "predicted": 7.9, "observed": 8.1, "measured": true, "source": "real-runs", "samples": 300},
     "secPerPromptToken": {"value": 1.2, "predicted": 1.1, "observed": 1.2, "measured": true, "source": "real-runs", "samples": 2927},
     "limit": "cpu"},
   "quality":  {"perplexity": 4.31, "tokens": 256, "measuredAt": "2026-10-30T17:00:00.0000000Z"},
   "fidelity": {"score": 1.0, "correct": 4, "total": 4, "promptTokens": 301, "version": 1, "measuredAt": "2026-10-30T17:05:00.0000000Z"},
   "queue": {"length": 0, "max": 8}}}
```

(The numbers above only show the shape; they are not measurements.)

| Key | Meaning |
| --- | --- |
| `architecture`, `quant`, `layers`, `fileBytes` | From the model file once it has loaded (`null` while loading). `quant` is the weight type holding the most bytes (`Q4_K` for a `Q4_K_M` file); `fileBytes` is the size on disk of every part |
| `contextLength`, `modelContextLength` | The context this server allows (the playbook's choice or `--context-length`) and the model's own maximum |
| `guessAhead` | Whether guess-ahead is on for greedy replies; `null` for a backend that has no such setting |
| `speed.secPerToken` | Seconds per reply token. `predicted` comes from `playbook probe` (reading vs computing every layer); `observed` is the average over real replies on this machine, published once they cover at least 20 tokens; `samples` is the reply tokens behind it. `value` is `observed` when there is one, else `predicted`, else `null` |
| `speed.secPerPromptToken` | The same for reading a prompt. `predicted` is the seconds per prompt token measured by `playbook eval`; `observed` is from real replies (prompt tokens that were not already in the KV cache) |
| `measured`, `source` | `measured` is true when `value` is not `null`. `source` says where `value` came from: `real-runs`, `probe` (token speed), `eval` (prompt speed) or `none` |
| `speed.limit` | `"disk"` or `"cpu"`: what limits a token, when a probe let the playbook name it, else `null` |
| `quality` | Perplexity of the model on the built-in passage and its token count, with when `playbook eval` ran (lower is better; compare quantizations of one model or models with the same tokenizer) |
| `fidelity` | The context-fidelity check (`playbook fidelity`): `correct` of `total` planted facts recalled, `score` = `correct / total`, the log's size in `promptTokens`, the check's `version` (scores of different versions do not compare) and when it ran. It is only as telling as the log is long: the default is a few hundred tokens |
| `queue` | Requests waiting behind the running one, and how many may wait |

A stub server (`--stub`) reports `null` for everything the model file or a measurement would supply.

## Errors

Errors are `{"error": {"message": ..., "type": ...}}` with real status codes: 400 for a bad request or an over-long
prompt (`context_length_exceeded`), 401 for a wrong key, 404 for an unknown model, 503 while loading
(`Retry-After: 30`), after a failed load, or when the queue is full (`Retry-After: 60`).

## Slow but alive

A streaming request gets its headers and an `: accepted` comment as soon as it is accepted, then a `: keepalive`
comment every 15 s while it waits in the queue or while a layer pass runs, so a router can tell slow from dead.

**One at a time.** Windlass runs one generation at a time; others wait in a FIFO queue and the log shows each one's
position. A streaming client that disconnects cancels its generation at the next layer, freeing the turn for the
next request.

**Follow-up turns are cheaper:** the KV cache of the previous prompt is kept, and a new prompt that starts the same
way (the earlier turns of a chat) skips that part.

## Register it in Fairlead

1. In Fairlead, **Providers → Add provider**, choose the **Custom (OpenAI-compatible)** preset.
2. Base URL `http://127.0.0.1:8777/v1`. Add the API key if you set one.
3. Tick **Local**, and set the timeout to **3600** seconds.
4. Fetch models; Fairlead reads the id and context length from `/v1/models`.
5. **Routes → Add route** named `deep-local`, with the Windlass model as its only target, hedging off, and local
   fallback off. Do not add Windlass to the `local` route or the auto rules: it is far too slow for interactive
   traffic, so only requests that ask for `deep-local` by name should reach it.

Windlass binds loopback only, as the suite requires, so Fairlead runs on the same PC.

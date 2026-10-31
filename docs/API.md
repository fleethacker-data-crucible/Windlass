# Windlass API

Windlass serves an OpenAI-compatible API on **`http://127.0.0.1:8777/v1`** (the first free port in its block,
8777-8779). It binds loopback only.

## Endpoints

- **`POST /v1/chat/completions`**: OpenAI Chat Completions, `stream: false` or `true`. Streams are SSE
  `data: {chat.completion.chunk}` lines ending with `data: [DONE]`. `stream_options.include_usage` adds a final chunk
  with `usage`; non-stream replies always carry `usage`. Honours `max_tokens` / `max_completion_tokens`,
  `temperature` (0 = greedy), `top_p`, `stop` and `seed`. Message `content` may be a string or an array of `text`
  parts. `tools` are ignored (logged); Windlass does not do tool calling.
- **`GET /v1/models`** and **`GET /v1/models/{id}`**: `{"object": "list", "data": [{"id": ..., "context_length": N, ...}]}`.
- **`GET /health`**: `status` (`loading`, `ready`, `error`), `busy`, `queued`.
- **`GET /__fleethacker/id`** and **`POST /__fleethacker/shutdown`**: the suite's self-detection endpoints.

## Errors

Errors are `{"error": {"message": ..., "type": ...}}` with real status codes: 400 for a bad request or an over-long
prompt (`context_length_exceeded`), 401 for a wrong key, 404 for an unknown model, 503 while loading
(`Retry-After: 30`), after a failed load, or when the queue is full (`Retry-After: 60`).

## Slow but alive

A streaming request gets its headers and an `: accepted` comment as soon as it is accepted, then a `: keepalive`
comment every 15 s while it waits in the queue or while a layer pass runs, so a router can tell slow from dead.

**One at a time (default).** Windlass runs one generation at a time; others wait in a FIFO queue and the log shows each
one's position. A streaming client that disconnects cancels its generation at the next layer, freeing the turn for the
next request.

**Several at once (`--batch N`).** With `serve --batch N` up to N generations run together, one decode step for all of
them per pass over the layers; request N+1 waits in the queue. Everything above is per request and unchanged: each
stream gets its own headers, `: accepted`, text chunks, usage and `[DONE]`, keepalives while it waits, and its own stop
sequences, `max_tokens` and `seed`; the reply is the same as when it runs alone. A client that disconnects cancels only
its own request (at the next step) and frees its slot; the others carry on. A queue beyond `--max-queue` still answers
503 `server_busy`. `/health` reports `busy` while any generation runs and `queued` for those waiting. A request that
joins a running batch waits for the current step, then its prompt runs as its own pass before it decodes with the
others. See [USAGE.md](USAGE.md#batching) for the RAM it adds.

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

## Fairlead's `deep-local` route

Fairlead treats Windlass as one more local OpenAI-compatible provider (route `deep-local`, target `windlass/<model>`;
Fairlead's `integrations/windlass/README.md` holds the contract and the registration steps). `tests\FairleadRouteTests.cs`
runs the real server against a stub model with the requests Fairlead sends (its client key as a Bearer token, the
`X-Fairlead-Privacy: local` header, a route header and request id, a body with extra fields Windlass ignores).

| Fairlead checklist item | Covered by |
| --- | --- |
| `GET /v1/models` lists the model and its context length | test: model list |
| A short non-stream request completes inside the timeout | test: non-stream request (the timeout itself is Fairlead's setting) |
| A streamed request survives a multi-minute layer pass (keepalives arrive) | test: headers and an SSE comment before the first token, keepalives while a slow pass runs, default interval under 60 s. **Hand check** for a real multi-minute pass on a big model |
| Fairlead's Activity shows the tokens and the long first-token time | test: usage on both reply kinds. **Hand check** of the Activity view in Fairlead |
| A second request while busy gets 503 (or queues) and Fairlead reports it clearly | test: 503 `server_busy` with a message and `Retry-After`, 503 `model_loading`, 404 unknown model, 400 bad request (Fairlead fails over on 5xx and does not retry 400) |

Also tested: the client key as a Bearer token when Windlass has `--api-key`, loopback `Host` names only, and Windlass's
port block (8777-8779, `src\FleetPorts.psd1`). Not tested here (needs Fairlead running): a live round trip through
Fairlead.

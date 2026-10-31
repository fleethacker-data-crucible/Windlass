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

## Lifecycle: heartbeat

`POST /__fleethacker/heartbeat` (with `X-FleetHacker-Heartbeat: 1`, body `{"client": "<name>", "leaving": false}`
optional) tells Windlass its owner is still there. Once any heartbeat has arrived, Windlass stops itself when every
client that sent one has gone quiet for 20 s or sent `"leaving": true` 5 s ago, never while a request is running or
queued. Clients that never send a heartbeat are unaffected. `WINDLASS_AUTO_STOP=0` or `--no-auto-stop` turns it off.
Details and the Fleet standard it follows: [LIFECYCLE.md](LIFECYCLE.md).

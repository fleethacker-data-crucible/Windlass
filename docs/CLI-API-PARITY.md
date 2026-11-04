# CLI and API parity

Windlass against the Fleet CLI parity proposal (comms `proposals/cli-parity`, v0.1, status: proposed). Windlass has a
browser UI ([UI.md](UI.md)) and every action in it is also a command or an API route; this page is the inventory the proposal's rollout asks for,
with the gaps stated. `Launch-Windlass.bat commands --json` (`windlass.commands/1`) is the registry; a test
(`tests\ParityTests.cs`) fails if a command or a route is missing from this page.

## Inventory

| Action | CLI command | API route | Output |
| --- | --- | --- | --- |
| Serve a model | `serve` | (the server itself) | OpenAI-compatible API on `127.0.0.1:8777-8779` |
| Chat completion, streamed or not | `run` (one reply in the console) | `POST /v1/chat/completions` | OpenAI chat completion or SSE chunks; `usage` on both |
| List the model | `info` (shape and RAM needs) | `GET /v1/models`, `GET /v1/models/{id}` | OpenAI model list with `context_length` |
| Inspect a model file | `info` | none (needs a file path, not a loaded model) | text |
| Measure this computer | `playbook probe` | none | `windlass.playbook/1` |
| Show the settings that would be used | `playbook show` | none | `windlass.playbook/1` |
| Score the model (perplexity) | `playbook eval` | none | `windlass.playbook/1` with quality |
| Check long-context recall | `playbook fidelity` | none | `windlass.fidelity/1`; the score is also recorded in the playbook and published in `GET /v1/models` |
| List commands | `commands [--json]` | none | `windlass.commands/1` |
| Health and load state | none | `GET /health` | JSON: state, model, busy, queued |
| Identify the tool, find its pid | none | `GET /__fleethacker/id` | JSON: tool, version, pid |
| Stop | `src\Stop-Windlass.ps1` | `POST /__fleethacker/shutdown` (header `X-FleetHacker-Shutdown: 1`), `POST /api/shutdown` (UI session token) | `{"stopping": true}` |
| Say that an owner is still there | none | `POST /__fleethacker/heartbeat` (header `X-FleetHacker-Heartbeat: 1`), `POST /api/heartbeat` (the UI's tab, `serve --open` only) | `{"ok": true, ...}` |
| Open the browser UI | `serve --open` (`--no-ui` turns the UI off) | `GET /` | the page |
| Get a UI session | none | `POST /api/session` (the API key when one is set) | `windlass.ui.session/1` |
| Show the live state | none | `GET /api/status` | `windlass.ui.status/1` (the `windlass` object of `GET /v1/models` plus state, queue, address) |
| Load, swap or unload the model | `serve --model` (at start) | `POST /api/model`, `POST /api/model/unload` | 202 `windlass.ui.model/1`; watch `GET /api/status` |
| Browse for a model file | none | `GET /api/models?dir=` | `windlass.ui.models/1` |
| Show the playbook in use | `playbook show` | `GET /api/playbook` | `windlass.ui.playbook/1` around `windlass.playbook/1` |
| Read the log | the file in `logs\` | `GET /api/logs?after=N` | `windlass.ui.logs/1` |

## Against the proposal's requirements

| # | Requirement | Windlass today |
| --- | --- | --- |
| 1 | No GUI-only code paths | **Met**: every page of the UI reads or calls a route above, and the commands that measure (`probe`, `eval`, `fidelity`) are not in the UI at all, only shown there |
| 2 | Named commands `<app> <noun> <verb>` | **Partly**: `playbook probe\|show\|eval` follow it; `serve`, `run`, `info` and `commands` are single words, and there is no `windlass <noun> <verb>` spelling for the API routes |
| 3 | `--json` with a versioned schema; `--out`, UTF-8 without BOM | **Partly**: `playbook *` and `commands` have `--json` and `--out`; `info` and `run` print text only (gap) |
| 4 | Open, documented project files | **Met** for the playbook (`playbooks\*.json`, schema `windlass.playbook/1`); Windlass has no other project files |
| 5 | Headless; `--yes`, `--dry-run`; loopback on the registry port | **Met** for headless and the port block; no command asks for confirmation, so `--yes` and `--dry-run` have nothing to do yet |
| 6 | Stable IDs across the Fleet | Not applicable (Windlass makes no shared assets) |
| 7 | `<app> commands --json` lists every command with inputs and outputs | **Met** for the CLI; the API routes are listed here, not in a generated OpenAPI file or MCP tools (gap) |
| 8 | A parity test in CI | **Partly**: `tests\ParityTests.cs` checks this page against `commands --json` and the routes in `src\Server.cs`; there are no GUI actions to register |
| 9 | Locked to this user: session token file, Host/Origin checks, no CORS, confirm on destructive calls | **Partly**: Host and Origin are checked on every request, a request with a non-loopback `Origin` gets 403, but a loopback page on any port is answered with CORS headers (the proposal says send none: a deviation, kept so local web UIs can call it), and an optional `--api-key` / `WINDLASS_API_KEY` bearer key exists. The UI's `/api/` routes need a per-session token (`POST /api/session`, which needs the API key when one is set) and answer this server's own origin only, but there is still **no per-session token file** for other programs (gap), and the only destructive route, shutdown, needs a custom header, not `confirm: true`. Researcher sign-off has not happened |

## Gaps to close (not done here)

1. `--json` and `--out` for `info` (and a JSON result for `run`).
2. A generated list of the API routes (OpenAPI) and MCP tools from the same registry as `commands`.
3. The per-session token file (requirement 9) and `confirm` on shutdown; both are decisions for the owner, since Fairlead and
   `Stop-Windlass.ps1` call these routes today.
4. An end-to-end agent job across Fleet apps using only the CLI or API (adoption checklist), which needs the other apps.

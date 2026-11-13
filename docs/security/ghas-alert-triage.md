# GHAS alert triage (sprint 2026-11)

Open code-scanning and Dependabot alerts reviewed for this sprint. Nothing here dismisses an alert on GitHub. Dismissal reasons are recommendations for the owner.

| Alert | Rule / package | Path | Disposition | Reason |
| --- | --- | --- | --- | --- |
| code-scanning 8 | `cs/user-controlled-bypass` | `src/Server.cs:337` | accept: false positive | One of the two path conditions on this line (`== "/v1/models"` or `StartsWith("/v1/models/")`). That branch calls `Authorized` before it returns a model. Recommend dismissal: False positive. |
| code-scanning 7 | `cs/user-controlled-bypass` | `src/Server.cs:337` | accept: false positive | The other path condition on this line. The model list is returned only from this handler, after `Authorized`. Recommend dismissal: False positive. |
| code-scanning 6 | `cs/user-controlled-bypass` | `src/Server.cs:413` | accept: false positive | `POST` is required before `/api/session` calls `Authorized` and returns the session token. Another method is 405 and returns no token. Recommend dismissal: False positive. |
| code-scanning 5 | `cs/user-controlled-bypass` | `src/Server.cs:411` | accept: false positive | The path selects `/api/session`. The session token is sent only from that handler, after `Authorized`. Recommend dismissal: False positive. |
| code-scanning 4 | `cs/user-controlled-bypass` | `src/Server.cs:356` | accept: false positive | `POST` is required before `/v1/chat/completions` calls `Authorized` and runs a completion. Another method is 405. Recommend dismissal: False positive. |
| code-scanning 3 | `cs/user-controlled-bypass` | `src/Server.cs:354` | accept: false positive | The path selects `/v1/chat/completions`. `ChatCompletions` runs only in that handler, after `Authorized`. Recommend dismissal: False positive. |
| code-scanning 2 | `cs/user-controlled-bypass` | `src/Server.cs:339` | accept: false positive | `GET` is required before `/v1/models` calls `Authorized` and returns models. Another method is 405. Recommend dismissal: False positive. |
| code-scanning 1 | `js/clear-text-storage-of-sensitive-data` | `ui-src/src/lib/prefs.svelte.ts` | fixed | The API key is kept in memory for the page only. It is not written to `sessionStorage` or `localStorage`; a reload asks for it again. A leftover `windlass-api-key` entry is removed and never read. |
| dependabot 7 | deepmerge (npm) `GHSA-gcqx-w48m-w2h4` | `ui-src/package-lock.json` | accept: won't fix within stack | Dev-only transitive of `@sveltejs/vite-plugin-svelte` (not in `ui/dist`). No patched release exists (latest 4.3.1). A downgrade to 4.1.2 is outside the advisory range and would revert the 4.2.0 enumerable-property change, so it is not a safe override. Recommend dismissal: Risk is tolerable to this project. |
| dependabot 6 | vite (npm) `GHSA-9jrq-w75r-8gcw` | `ui-src/package-lock.json` | fixed | Direct devDependency is 8.3.8 (first patched release is 8.3.3; 8.3.8 matches Lazarette-Lite). |
| dependabot 5 | vite (npm) `GHSA-rq7h-c2jc-7f22` | `ui-src/package-lock.json` | fixed | Same Vite 8.3.8 pin. |
| dependabot 4 | vite (npm) `GHSA-vfpm-58rq-9qcg` | `ui-src/package-lock.json` | fixed | Same Vite 8.3.8 pin. |
| dependabot 3 | vite (npm) `GHSA-r825-w8mm-5pxw` | `ui-src/package-lock.json` | fixed | Same Vite 8.3.8 pin. |
| dependabot 2 | vite (npm) `GHSA-587h-7wmj-h5pm` | `ui-src/package-lock.json` | fixed | Same Vite 8.3.8 pin. |
| dependabot 1 | vite (npm) `GHSA-698v-4r3x-3vcv` | `ui-src/package-lock.json` | fixed | Same Vite 8.3.8 pin. |

## `cs/user-controlled-bypass` (alerts 2–8)

CodeQL treats `Authorized` as a sensitive method because the name matches `auth`. Each alert is a request path or HTTP method condition that decides whether that call runs. The call sits inside the handler it guards:

- `/v1/models` and `/v1/models/{id}` return model data only after `Authorized`.
- `/v1/chat/completions` runs a completion only after `Authorized`.
- `/api/session` returns the session token only after `Authorized`.

Choosing another path selects another handler (or 404). It does not skip the check and still perform that action. `tests/Tests.cs` (`api: API key`) covers the key on those routes, including a trailing slash, a model id, the wrong method, and `/api/session`.

`/health`, `GET /__fleethacker/id`, and the fleet heartbeat and shutdown routes stay open on loopback when an API key is set. That is the documented contract (`docs/API.md`), and these seven alerts do not point at those routes. When no API key is configured, `Authorized` allows the request. That is also the documented contract.

## deepmerge

`deepmerge@4.3.1` is required by `@sveltejs/vite-plugin-svelte@7.3.1` and is marked `dev: true` in the lockfile. The built page under `ui/dist` does not contain it. The plugin merges its own config while the UI is built. npm has no release newer than 4.3.1, and [GHSA-gcqx-w48m-w2h4](https://github.com/advisories/GHSA-gcqx-w48m-w2h4) lists no patched version (`>= 4.2.0, <= 4.3.1`). The last release outside that range is 4.1.2. Pinning that with an override would undo the 4.2.0 change that stops overwriting inherited enumerable properties, so it is not used.

CVE-2026-93753 was rejected by its CNA on 2026-11-02 as not describing a security vulnerability. The GitHub advisory was still open when this sprint was triaged, so Dependabot still reports it. If the advisory is withdrawn, dismiss as inaccurate. Until then, dismiss as tolerable risk.

## API key in the browser

`sessionStorage` is still clear-text storage, so leaving the key there would not clear `js/clear-text-storage-of-sensitive-data`. The page now holds the key on `credentials.apiKey` for the life of the page, sends it only as `Authorization: Bearer` to this server, and removes any leftover `windlass-api-key` entry without reading it. Theme, sidebar, and max-tokens preferences stay in `localStorage`. They are not secrets.

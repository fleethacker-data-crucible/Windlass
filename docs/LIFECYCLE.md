# Lifecycle

How Windlass starts, stays and stops, against the Fleet local app lifecycle standard (Fairlead's
`docs/LOCAL_APP_LIFECYCLE.md`). Windlass is an API server with a browser page of its own (since the UI, [UI.md](UI.md)), so the browser rules apply to
that page when it owns the server, and otherwise map to "whatever started it": a launcher, a script or Fairlead.

| # | Rule in the standard | Windlass |
| --- | --- | --- |
| 1 | The owner's heartbeat owns the engine's life: stop after 20 s without one, or 5 s after a leaving signal | **Yes for the UI tab of `serve --open`:** `POST /api/heartbeat` every 5 s, a `sendBeacon` leaving signal on `pagehide`. A server started without `--open` is not owned by a tab, so opening and closing the page never stops a Windlass that Fairlead or a script uses. **Opt-in by use for other programs.** `POST /__fleethacker/heartbeat` (below). Nothing changes for a client that never sends one: auto-stop only arms at the first heartbeat |
| 2 | Never stop mid-write | **Yes.** A running or queued request holds the stop; it happens on the first check after the last one ends |
| 3 | An explicit way out in the UI | **Yes.** *End session* in the sidebar and in Settings, behind a confirm (`POST /api/shutdown`). `POST /__fleethacker/shutdown` is the way out for a program such as Fairlead |
| 4 | A `Stop-<App>.ps1` that needs no PIDs | **Yes.** `src\Stop-Windlass.ps1` |
| 5 | One instance: detect a running engine, `--replace`, `--stay` | **Partly.** `Start-Windlass.ps1` reuses the port block with the Fleet port helper and reports a running instance of the same version; it does not ask to replace it and has no `--replace` or `--stay` (a server that never receives a heartbeat already stays) |
| 6 | The build stops the app | Not applicable (no build step; C# is compiled at start) |
| 7 | A runtime file per engine | **Yes.** `logs\windlass-server-<pid>.json` (pid, port, url, start time), removed on stop, stale files removed by the stop script |
| 8 | An off switch | **Yes.** `WINDLASS_AUTO_STOP=0` or `serve --no-auto-stop` (the standard's `--stay`) |

## The heartbeat

```
POST /__fleethacker/heartbeat
X-FleetHacker-Heartbeat: 1
{"client": "fairlead", "leaving": false}      (body optional)
```

The header is required, like `X-FleetHacker-Shutdown` on the shutdown route, so a web page cannot send it cross-site.
The answer is `{"ok": true, "autoStop": true, "timeoutSeconds": 20, "leavingGraceSeconds": 5}`. A body that is not JSON
is ignored.

- Send one every 5 seconds. Each `client` name is tracked separately (an anonymous client has an empty name).
- Send `"leaving": true` when the client goes. A new heartbeat from the same client inside 5 seconds cancels it (a page
  reload).
- Windlass stops when it has heard from at least one client and every client is gone: 20 s of silence, or 5 s after its
  leaving signal.
- A client that is still silent but a request is running or queued: Windlass waits and stops on the first check after
  the request ends. A client that heartbeats again in the meantime keeps it alive.

The rule is `src\Presence.cs`; tests drive it with a fake clock (`tests\PresenceTests.cs`).

## The tray icon

`serve --open` in a desktop session shows a notification-area icon (`src\Tray.ps1`) and starts the engine with `--tray`.
With the icon, rule 1 changes for the UI tab: the browser tab does **not** own the server. Closing the window or the
tab leaves the engine and the loaded model running, because Fairlead and other programs call the local API. The icon
owns the engine's life instead:

| Icon action | Effect |
| --- | --- |
| Double-click, or *Open Windlass* | Opens the UI in an Edge or Chrome `--app` window (the default browser if neither is installed) |
| Status line (disabled) and tooltip | `Running on 127.0.0.1:PORT, model: NAME` (`none` when idle, `loading NAME` while a model loads) |
| *Quit Windlass* | Asks once ("programs that use it will stop working"), then stops the engine, unloads the model, removes the runtime file and the icon |

Other ways out still work and remove the icon: *End session* in the UI, `src\Stop-Windlass.ps1`, Ctrl+C in the console.
Starting Windlass again finds the running instance (the Fleet `/__fleethacker/id` check) and only reopens the window.
`--no-tray` restores the old behaviour (the tab owns the server). Programs that send heartbeats (Fairlead) can still own
the engine as before; the tab's heartbeats are simply ignored in tray mode. Closing the console window that shows the
log ends the process, so use *Quit* or minimise it.

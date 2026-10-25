"""OpenAI-compatible HTTP API in front of a :class:`~windlass.backend.Backend`.

Endpoints: ``POST /v1/chat/completions`` (streaming and not), ``GET /v1/models``,
``GET /v1/models/{id}`` and ``GET /health``.

Windlass serves one generation at a time. Others wait in a FIFO queue (bounded by
``max_queue``; beyond that the server answers 503). A streaming request gets its response
headers and an SSE comment as soon as it is accepted, then ``: keepalive`` comments while it
waits in the queue or while a slow layer pass runs, so a router can tell slow from dead.
"""

from __future__ import annotations

import asyncio
import json
import logging
import threading
import time
import uuid
from contextlib import asynccontextmanager
from dataclasses import dataclass
from typing import AsyncIterator

from fastapi import FastAPI, Request
from fastapi.responses import JSONResponse, StreamingResponse
from starlette.exceptions import HTTPException as StarletteHTTPException

from . import __version__
from .backend import Backend, BackendError, GenerationRequest, GenerationResult

log = logging.getLogger("windlass.server")


@dataclass
class ServerSettings:
    keepalive_interval: float = 15.0   # seconds between ": keepalive" comments; keep < 60
    max_queue: int = 8                 # requests allowed to wait behind the running one
    api_key: str | None = None         # require "Authorization: Bearer <key>" when set
    default_max_tokens: int = 1024
    load_on_startup: bool = True


def error_response(status: int, message: str, type: str, headers: dict | None = None):
    return JSONResponse({"error": {"message": message, "type": type}}, status_code=status,
                        headers=headers)


def sse(data: dict | str) -> str:
    return f"data: {data if isinstance(data, str) else json.dumps(data, ensure_ascii=False)}\n\n"


class Scheduler:
    """One running job, a bounded FIFO of waiting ones."""

    def __init__(self, max_queue: int):
        self.max_queue = max_queue
        self._lock = asyncio.Lock()
        self.waiting = 0

    @property
    def busy(self) -> bool:
        return self._lock.locked()

    def reserve(self, rid: str) -> int:
        """Claim a place in line now (so a full queue is a clean 503); returns the position."""
        if self.busy and self.waiting >= self.max_queue:
            raise BackendError(f"Windlass is busy and its queue of {self.max_queue} is full",
                               503, "server_busy")
        self.waiting += 1
        position = self.waiting if self.busy else 0
        if position:
            log.info("%s queued at position %d", rid, position)
        return position

    async def acquire(self) -> None:
        await self._lock.acquire()

    def leave_queue(self) -> None:
        """Give up the place claimed with :meth:`reserve` (on getting the turn or leaving)."""
        self.waiting -= 1

    def release(self) -> None:
        self._lock.release()


def parse_request(body, backend: Backend, settings: ServerSettings) -> GenerationRequest:
    if not isinstance(body, dict):
        raise BackendError("request body must be a JSON object")
    model = body.get("model")
    if model not in (None, "", backend.model_id):
        raise BackendError(f"model {model!r} not found; this server has {backend.model_id!r}",
                           404, "not_found_error")
    messages = body.get("messages")
    if not isinstance(messages, list) or not messages:
        raise BackendError("'messages' must be a non-empty array")
    for m in messages:
        if not isinstance(m, dict) or not isinstance(m.get("role"), str):
            raise BackendError("each message needs a 'role'")
    if body.get("n", 1) != 1:
        raise BackendError("only n=1 is supported")
    if body.get("tools"):
        log.warning("request carries tools; Windlass does not do tool calling and ignores them")

    max_tokens = body.get("max_completion_tokens")
    if max_tokens is None:
        max_tokens = body.get("max_tokens")
    if max_tokens is None:
        max_tokens = settings.default_max_tokens
    if not isinstance(max_tokens, int) or isinstance(max_tokens, bool) or max_tokens < 1:
        raise BackendError("'max_tokens' must be a positive integer")

    def number(key, default, low, high):
        value = body.get(key)
        if value is None:
            return default
        if not isinstance(value, (int, float)) or not low <= value <= high:
            raise BackendError(f"'{key}' must be a number between {low} and {high}")
        return float(value)

    stop = body.get("stop") or []
    if isinstance(stop, str):
        stop = [stop]
    if not isinstance(stop, list) or not all(isinstance(s, str) for s in stop):
        raise BackendError("'stop' must be a string or an array of strings")
    seed = body.get("seed")
    if seed is not None and not isinstance(seed, int):
        raise BackendError("'seed' must be an integer")
    return GenerationRequest(
        messages=messages, max_tokens=max_tokens,
        temperature=number("temperature", 1.0, 0, 2), top_p=number("top_p", 1.0, 0, 1),
        stop=stop, seed=seed)


def create_app(backend: Backend, settings: ServerSettings | None = None) -> FastAPI:
    settings = settings or ServerSettings()
    scheduler: Scheduler | None = None

    def load() -> None:
        try:
            backend.load()
        except Exception as exc:  # reported on every request until restart
            backend.state = "error"
            backend.load_error = f"{type(exc).__name__}: {exc}"
            log.exception("model failed to load")

    @asynccontextmanager
    async def lifespan(app: FastAPI):
        nonlocal scheduler
        scheduler = Scheduler(settings.max_queue)
        if settings.load_on_startup and backend.state == "loading":
            threading.Thread(target=load, name="windlass-load", daemon=True).start()
        yield
        backend.close()

    app = FastAPI(title="Windlass", version=__version__, lifespan=lifespan,
                  docs_url=None, redoc_url=None, openapi_url=None)
    app.state.backend = backend
    app.state.settings = settings

    @app.exception_handler(StarletteHTTPException)
    async def http_error(request: Request, exc: StarletteHTTPException):
        kind = "not_found_error" if exc.status_code == 404 else "invalid_request_error"
        return error_response(exc.status_code, str(exc.detail), kind)

    @app.exception_handler(Exception)
    async def unhandled(request: Request, exc: Exception):
        log.exception("unhandled error")
        return error_response(500, f"{type(exc).__name__}: {exc}", "server_error")

    def check_auth(request: Request):
        if not settings.api_key:
            return None
        header = request.headers.get("authorization", "")
        key = header[7:] if header.lower().startswith("bearer ") else request.headers.get("x-api-key")
        if key != settings.api_key:
            return error_response(401, "missing or wrong API key", "authentication_error")
        return None

    def model_card() -> dict:
        return {"id": backend.model_id, "object": "model", "created": 0,
                "owned_by": backend.owned_by, "context_length": backend.context_length,
                "status": backend.state}

    @app.get("/health")
    async def health():
        return {"status": backend.state, "model": backend.model_id,
                "busy": scheduler.busy if scheduler else False,
                "queued": scheduler.waiting if scheduler else 0,
                "error": backend.load_error}

    @app.get("/v1/models")
    async def models(request: Request):
        if (denied := check_auth(request)) is not None:
            return denied
        return {"object": "list", "data": [model_card()]}

    @app.get("/v1/models/{model_id:path}")
    async def model(model_id: str, request: Request):
        if (denied := check_auth(request)) is not None:
            return denied
        if model_id != backend.model_id:
            return error_response(404, f"model {model_id!r} not found", "not_found_error")
        return model_card()

    async def run_job(rid: str, greq: GenerationRequest, cancel: threading.Event,
                      tick: float) -> AsyncIterator[tuple]:
        """Wait for a turn, run the backend on a thread, and yield its events.

        Yields ``("tick",)`` every ``tick`` seconds of silence, ``("text", str)`` and finally
        ``("done", GenerationResult)``. Raises :class:`BackendError` on failure.
        """
        assert scheduler is not None
        turn = asyncio.ensure_future(scheduler.acquire())
        try:
            while not turn.done():
                await asyncio.wait({turn}, timeout=tick)
                if not turn.done():
                    yield ("tick",)
        except BaseException:
            turn.cancel()
            try:
                await turn
                scheduler.release()  # got the lock in the race; hand it straight back
            except (asyncio.CancelledError, Exception):
                pass
            raise
        finally:
            scheduler.leave_queue()

        loop = asyncio.get_running_loop()
        events: asyncio.Queue = asyncio.Queue()
        started = time.monotonic()
        passes = [0]

        def put(item):
            loop.call_soon_threadsafe(events.put_nowait, item)

        def on_layer(index: int, total: int):
            if index == total:
                passes[0] += 1
            log.debug("%s layer %d/%d", rid, index, total)

        def worker():
            try:
                put(("done", backend.generate(greq, lambda t: put(("text", t)), on_layer, cancel)))
            except BackendError as exc:
                put(("error", exc))
            except Exception as exc:
                log.exception("%s generation failed", rid)
                put(("error", BackendError(f"{type(exc).__name__}: {exc}", 500, "server_error")))
            finally:
                loop.call_soon_threadsafe(scheduler.release)

        log.info("%s started (%d waiting)", rid, scheduler.waiting)
        threading.Thread(target=worker, name=f"windlass-{rid}", daemon=True).start()
        try:
            while True:
                try:
                    item = await asyncio.wait_for(events.get(), timeout=tick)
                except asyncio.TimeoutError:
                    yield ("tick",)
                    continue
                if item[0] == "error":
                    raise item[1]
                if item[0] == "text" and not item[1]:
                    continue
                if item[0] == "done":
                    result: GenerationResult = item[1]
                    elapsed = time.monotonic() - started
                    log.info("%s %s: %d prompt + %d completion tokens in %.1fs (%.2f tok/s)",
                             rid, result.finish_reason, result.prompt_tokens,
                             result.completion_tokens, elapsed,
                             result.completion_tokens / elapsed if elapsed else 0.0)
                yield item
                if item[0] == "done":
                    return
        finally:
            cancel.set()  # no-op when finished; stops the worker after a disconnect

    @app.post("/v1/chat/completions")
    async def chat_completions(request: Request):
        if (denied := check_auth(request)) is not None:
            return denied
        try:
            body = await request.json()
        except (json.JSONDecodeError, UnicodeDecodeError):
            return error_response(400, "request body is not valid JSON", "invalid_request_error")
        try:
            greq = parse_request(body, backend, settings)
        except BackendError as exc:
            return error_response(exc.status, str(exc), exc.type)
        if backend.state == "loading":
            return error_response(503, f"{backend.model_id} is still loading", "model_loading",
                                  {"Retry-After": "30"})
        if backend.state == "error":
            return error_response(503, f"{backend.model_id} failed to load: {backend.load_error}",
                                  "model_load_failed")
        assert scheduler is not None
        rid = "chatcmpl-" + uuid.uuid4().hex[:24]
        try:
            scheduler.reserve(rid)
        except BackendError as exc:
            return error_response(exc.status, str(exc), exc.type, {"Retry-After": "60"})

        created = int(time.time())
        cancel = threading.Event()
        stream = bool(body.get("stream"))
        include_usage = bool((body.get("stream_options") or {}).get("include_usage"))

        def usage(result: GenerationResult) -> dict:
            return {"prompt_tokens": result.prompt_tokens,
                    "completion_tokens": result.completion_tokens,
                    "total_tokens": result.prompt_tokens + result.completion_tokens}

        if not stream:
            parts: list[str] = []
            try:
                async for item in run_job(rid, greq, cancel, 1.0):
                    if item[0] == "tick" and await request.is_disconnected():
                        log.info("%s client went away; cancelling", rid)
                        cancel.set()
                    elif item[0] == "text":
                        parts.append(item[1])
                    elif item[0] == "done":
                        result = item[1]
            except BackendError as exc:
                return error_response(exc.status, str(exc), exc.type)
            return {
                "id": rid, "object": "chat.completion", "created": created,
                "model": backend.model_id,
                "choices": [{"index": 0, "finish_reason": _finish(result),
                             "message": {"role": "assistant", "content": "".join(parts)}}],
                "usage": usage(result),
            }

        def chunk(delta: dict, finish: str | None = None) -> str:
            return sse({"id": rid, "object": "chat.completion.chunk", "created": created,
                        "model": backend.model_id,
                        "choices": [{"index": 0, "delta": delta, "finish_reason": finish}]})

        async def events():
            # Sent before any waiting, so the headers and a first byte reach the client now.
            yield ": accepted\n\n"
            try:
                yield chunk({"role": "assistant", "content": ""})
                async for item in run_job(rid, greq, cancel, settings.keepalive_interval):
                    if item[0] == "tick":
                        yield ": keepalive\n\n"
                    elif item[0] == "text":
                        yield chunk({"content": item[1]})
                    elif item[0] == "done":
                        result = item[1]
                        yield chunk({}, _finish(result))
                        if include_usage:
                            yield sse({"id": rid, "object": "chat.completion.chunk",
                                       "created": created, "model": backend.model_id,
                                       "choices": [], "usage": usage(result)})
            except BackendError as exc:
                yield sse({"error": {"message": str(exc), "type": exc.type}})
            except asyncio.CancelledError:
                log.info("%s client went away; cancelling", rid)
                raise
            yield sse("[DONE]")

        return StreamingResponse(events(), media_type="text/event-stream",
                                 headers={"Cache-Control": "no-cache", "X-Accel-Buffering": "no"})

    return app


def _finish(result: GenerationResult) -> str:
    # A cancelled job only finishes after its client left, so this is rarely seen.
    return "length" if result.finish_reason == "length" else "stop"

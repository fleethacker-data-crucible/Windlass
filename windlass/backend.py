"""The interface between the HTTP server and whatever produces tokens.

The server only knows about :class:`Backend`. The real one is
:class:`windlass.engine.backend.LayeredBackend`; tests use :class:`StubBackend`, which needs
no torch and can be made as slow as a real 70B model to exercise keepalives and queueing.
"""

from __future__ import annotations

import threading
import time
from dataclasses import dataclass, field
from typing import Callable, Literal


class BackendError(Exception):
    """A request the backend cannot serve. ``status`` becomes the HTTP status code."""

    def __init__(self, message: str, status: int = 400, type: str = "invalid_request_error"):
        super().__init__(message)
        self.status = status
        self.type = type


@dataclass
class GenerationRequest:
    messages: list[dict]
    max_tokens: int
    temperature: float = 1.0
    top_p: float = 1.0
    stop: list[str] = field(default_factory=list)
    seed: int | None = None


@dataclass
class GenerationResult:
    finish_reason: Literal["stop", "length", "cancelled"]
    prompt_tokens: int
    completion_tokens: int


# A backend reports progress through these callbacks, called from its worker thread.
OnText = Callable[[str], None]
OnLayer = Callable[[int, int], None]


class Backend:
    model_id: str = "model"
    context_length: int = 4096
    state: Literal["loading", "ready", "error"] = "loading"
    load_error: str | None = None
    owned_by: str = "windlass"

    def load(self) -> None:
        """Load the model. Blocking; the server calls it on a background thread."""
        raise NotImplementedError

    def generate(self, request: GenerationRequest, on_text: OnText, on_layer: OnLayer,
                 cancel: threading.Event) -> GenerationResult:
        """Generate a reply, calling ``on_text`` with each new piece of text. Blocking."""
        raise NotImplementedError

    def close(self) -> None:
        pass


class StopScanner:
    """Holds back streamed text that could be the start of a stop sequence.

    ``feed`` returns the text that is safe to send now and whether a stop sequence was hit
    (in which case the stop sequence itself and anything after it are dropped).
    """

    def __init__(self, stops: list[str]):
        self.stops = [s for s in stops if s]
        self.hold = max((len(s) for s in self.stops), default=1) - 1
        self.buffer = ""

    def feed(self, text: str) -> tuple[str, bool]:
        self.buffer += text
        cut = min((i for i in (self.buffer.find(s) for s in self.stops) if i >= 0), default=-1)
        if cut >= 0:
            out, self.buffer = self.buffer[:cut], ""
            return out, True
        if self.hold == 0:
            out, self.buffer = self.buffer, ""
        else:
            out, self.buffer = self.buffer[:-self.hold], self.buffer[-self.hold:]
        return out, False

    def flush(self) -> str:
        out, self.buffer = self.buffer, ""
        return out


class StubBackend(Backend):
    """Replies with a fixed sentence, one word per "layer pass"."""

    def __init__(self, model_id: str = "stub", reply: str = "Hello from Windlass, layer by layer.",
                 token_delay: float = 0.0, load_delay: float = 0.0, context_length: int = 4096,
                 fail_with: BackendError | None = None):
        self.model_id = model_id
        self.reply = reply
        self.token_delay = token_delay
        self.load_delay = load_delay
        self.context_length = context_length
        self.fail_with = fail_with
        self.calls: list[GenerationRequest] = []
        self.cancelled = threading.Event()

    def load(self) -> None:
        time.sleep(self.load_delay)
        self.state = "ready"

    def generate(self, request, on_text, on_layer, cancel):
        self.calls.append(request)
        if self.fail_with is not None:
            raise self.fail_with
        prompt_tokens = sum(len(str(m.get("content", "")).split()) for m in request.messages)
        words = self.reply.split(" ")
        scanner = StopScanner(request.stop)
        produced = 0
        for i, word in enumerate(words):
            if produced >= request.max_tokens:
                on_text(scanner.flush())
                return GenerationResult("length", prompt_tokens, produced)
            end = time.monotonic() + self.token_delay
            while time.monotonic() < end:
                if cancel.is_set():
                    self.cancelled.set()
                    return GenerationResult("cancelled", prompt_tokens, produced)
                time.sleep(min(0.01, self.token_delay))
            if cancel.is_set():
                self.cancelled.set()
                return GenerationResult("cancelled", prompt_tokens, produced)
            on_layer(1, 1)
            produced += 1
            text, stopped = scanner.feed(word if i == 0 else " " + word)
            if text:
                on_text(text)
            if stopped:
                return GenerationResult("stop", prompt_tokens, produced)
        on_text(scanner.flush())
        return GenerationResult("stop", prompt_tokens, produced)

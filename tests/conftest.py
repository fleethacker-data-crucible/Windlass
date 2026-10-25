import socket
import threading
import time

import httpx
import pytest
import uvicorn

from windlass.backend import StubBackend
from windlass.server import ServerSettings, create_app


class LiveServer:
    """A real uvicorn server on a free port, so streaming and disconnects behave for real."""

    def __init__(self, backend, settings):
        with socket.socket() as s:
            s.bind(("127.0.0.1", 0))
            self.port = s.getsockname()[1]
        self.backend = backend
        self.url = f"http://127.0.0.1:{self.port}"
        config = uvicorn.Config(create_app(backend, settings), host="127.0.0.1", port=self.port,
                                log_level="warning")
        self.server = uvicorn.Server(config)
        self.thread = threading.Thread(target=self.server.run, daemon=True)

    def __enter__(self):
        self.thread.start()
        deadline = time.monotonic() + 10
        while not self.server.started:
            if time.monotonic() > deadline:
                raise RuntimeError("server did not start")
            time.sleep(0.02)
        return self

    def __exit__(self, *exc):
        self.server.should_exit = True
        self.thread.join(10)

    def client(self, **kw):
        return httpx.Client(base_url=self.url, timeout=kw.pop("timeout", 10), **kw)


@pytest.fixture
def live():
    """Factory: ``with live(backend, **settings) as srv: ...``."""

    def make(backend=None, **settings):
        backend = backend or StubBackend()
        srv = LiveServer(backend, ServerSettings(**settings))
        return srv

    return make


def wait_ready(srv, timeout=5):
    deadline = time.monotonic() + timeout
    with srv.client() as c:
        while c.get("/health").json()["status"] != "ready":
            if time.monotonic() > deadline:
                raise RuntimeError("backend never became ready")
            time.sleep(0.02)

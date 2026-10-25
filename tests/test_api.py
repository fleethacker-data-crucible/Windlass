"""The HTTP contract Fairlead relies on, exercised against a stub model."""

import json
import threading
import time

import httpx
import pytest

from conftest import wait_ready
from windlass.backend import BackendError, StubBackend, StopScanner

CHAT = {"model": "stub", "messages": [{"role": "user", "content": "hi there"}]}


def read_sse(response):
    """Return (comments, data payloads) from a streaming response."""
    comments, data = [], []
    for line in response.iter_lines():
        if line.startswith(":"):
            comments.append(line[1:].strip())
        elif line.startswith("data: "):
            payload = line[6:]
            data.append(payload if payload == "[DONE]" else json.loads(payload))
    return comments, data


def test_models_lists_id_and_context_length(live):
    with live(StubBackend(context_length=8192)) as srv, srv.client() as c:
        wait_ready(srv)
        body = c.get("/v1/models").json()
        assert body["object"] == "list"
        assert body["data"][0]["id"] == "stub"
        assert body["data"][0]["context_length"] == 8192
        assert c.get("/v1/models/stub").json()["id"] == "stub"
        missing = c.get("/v1/models/nope")
        assert missing.status_code == 404
        assert missing.json()["error"]["type"] == "not_found_error"


def test_non_stream_returns_message_and_usage(live):
    with live() as srv, srv.client() as c:
        wait_ready(srv)
        r = c.post("/v1/chat/completions", json=CHAT)
        assert r.status_code == 200
        body = r.json()
        assert body["object"] == "chat.completion"
        assert body["choices"][0]["message"] == {
            "role": "assistant", "content": "Hello from Windlass, layer by layer."}
        assert body["choices"][0]["finish_reason"] == "stop"
        assert body["usage"] == {"prompt_tokens": 2, "completion_tokens": 6, "total_tokens": 8}


def test_stream_chunks_usage_and_done(live):
    with live() as srv, srv.client() as c:
        wait_ready(srv)
        req = {**CHAT, "stream": True, "stream_options": {"include_usage": True}}
        with c.stream("POST", "/v1/chat/completions", json=req) as r:
            assert r.status_code == 200
            assert r.headers["content-type"].startswith("text/event-stream")
            comments, data = read_sse(r)
        assert comments[0] == "accepted"
        assert data[-1] == "[DONE]"
        chunks = data[:-1]
        assert all(ch["object"] == "chat.completion.chunk" for ch in chunks)
        assert chunks[0]["choices"][0]["delta"]["role"] == "assistant"
        text = "".join(ch["choices"][0]["delta"].get("content", "")
                       for ch in chunks if ch["choices"])
        assert text == "Hello from Windlass, layer by layer."
        finals = [ch for ch in chunks if ch["choices"] and ch["choices"][0]["finish_reason"]]
        assert [f["choices"][0]["finish_reason"] for f in finals] == ["stop"]
        usage = chunks[-1]
        assert usage["choices"] == []
        assert usage["usage"]["prompt_tokens"] == 2
        assert usage["usage"]["completion_tokens"] == 6


def test_stream_without_include_usage_has_no_usage_chunk(live):
    with live() as srv, srv.client() as c:
        wait_ready(srv)
        with c.stream("POST", "/v1/chat/completions", json={**CHAT, "stream": True}) as r:
            _, data = read_sse(r)
        assert all("usage" not in ch for ch in data[:-1])


def test_max_tokens_finishes_with_length(live):
    with live() as srv, srv.client() as c:
        wait_ready(srv)
        body = c.post("/v1/chat/completions", json={**CHAT, "max_tokens": 2}).json()
        assert body["choices"][0]["message"]["content"] == "Hello from"
        assert body["choices"][0]["finish_reason"] == "length"
        assert body["usage"]["completion_tokens"] == 2


def test_stop_sequence_is_removed(live):
    with live() as srv, srv.client() as c:
        wait_ready(srv)
        body = c.post("/v1/chat/completions", json={**CHAT, "stop": [", layer"]}).json()
        assert body["choices"][0]["message"]["content"] == "Hello from Windlass"
        assert body["choices"][0]["finish_reason"] == "stop"


@pytest.mark.parametrize("payload,status", [
    ({**CHAT, "model": "unknown-model"}, 404),
    ({"model": "stub", "messages": []}, 400),
    ({"model": "stub", "messages": [{"content": "no role"}]}, 400),
    ({**CHAT, "n": 2}, 400),
    ({**CHAT, "temperature": 9}, 400),
    ({**CHAT, "max_tokens": 0}, 400),
])
def test_bad_requests_get_openai_errors(live, payload, status):
    with live() as srv, srv.client() as c:
        wait_ready(srv)
        r = c.post("/v1/chat/completions", json=payload)
        assert r.status_code == status
        err = r.json()["error"]
        assert err["message"] and err["type"]


def test_invalid_json_is_400(live):
    with live() as srv, srv.client() as c:
        r = c.post("/v1/chat/completions", content=b"{not json",
                   headers={"content-type": "application/json"})
        assert r.status_code == 400
        assert r.json()["error"]["type"] == "invalid_request_error"


def test_503_while_loading(live):
    with live(StubBackend(load_delay=3)) as srv, srv.client() as c:
        r = c.post("/v1/chat/completions", json=CHAT)
        assert r.status_code == 503
        assert r.json()["error"]["type"] == "model_loading"
        assert c.get("/v1/models").json()["data"][0]["status"] == "loading"


def test_503_when_load_failed(live):
    class Broken(StubBackend):
        def load(self):
            raise RuntimeError("disk on fire")

    with live(Broken()) as srv, srv.client() as c:
        deadline = time.monotonic() + 5
        while c.get("/health").json()["status"] != "error" and time.monotonic() < deadline:
            time.sleep(0.02)
        r = c.post("/v1/chat/completions", json=CHAT)
        assert r.status_code == 503
        assert "disk on fire" in r.json()["error"]["message"]


def test_stream_headers_arrive_before_first_token(live):
    with live(StubBackend(token_delay=2)) as srv, srv.client(timeout=30) as c:
        wait_ready(srv)
        started = time.monotonic()
        with c.stream("POST", "/v1/chat/completions", json={**CHAT, "stream": True}) as r:
            assert r.status_code == 200
            first = next(r.iter_lines())
            assert time.monotonic() - started < 1.0
            assert first == ": accepted"


def test_keepalive_comments_during_slow_pass(live):
    backend = StubBackend(reply="slow token", token_delay=0.6)
    with live(backend, keepalive_interval=0.1) as srv, srv.client() as c:
        wait_ready(srv)
        with c.stream("POST", "/v1/chat/completions", json={**CHAT, "stream": True}) as r:
            comments, data = read_sse(r)
        assert comments.count("keepalive") >= 3
        assert data[-1] == "[DONE]"


def test_requests_queue_and_run_one_at_a_time(live):
    backend = StubBackend(reply="a b c", token_delay=0.15)
    active, peak, lock = [0], [0], threading.Lock()
    original = backend.generate

    def tracked(*args):
        with lock:
            active[0] += 1
            peak[0] = max(peak[0], active[0])
        try:
            return original(*args)
        finally:
            with lock:
                active[0] -= 1

    backend.generate = tracked
    with live(backend) as srv:
        wait_ready(srv)
        results = []

        def call():
            with srv.client() as c:
                results.append(c.post("/v1/chat/completions", json=CHAT))

        threads = [threading.Thread(target=call) for _ in range(3)]
        for t in threads:
            t.start()
        for t in threads:
            t.join()
        assert [r.status_code for r in results] == [200, 200, 200]
        assert peak[0] == 1


def test_full_queue_is_503(live):
    backend = StubBackend(reply="a b c d", token_delay=0.5)
    with live(backend, max_queue=0) as srv:
        wait_ready(srv)
        first = threading.Thread(
            target=lambda: srv.client().post("/v1/chat/completions", json=CHAT))
        first.start()
        with srv.client() as c:
            deadline = time.monotonic() + 5
            while not c.get("/health").json()["busy"] and time.monotonic() < deadline:
                time.sleep(0.01)
            r = c.post("/v1/chat/completions", json=CHAT)
        first.join()
        assert r.status_code == 503
        assert r.json()["error"]["type"] == "server_busy"


def test_client_disconnect_cancels_generation(live):
    backend = StubBackend(reply="one two three four five six", token_delay=0.3)
    with live(backend) as srv:
        wait_ready(srv)
        with srv.client() as c:
            with c.stream("POST", "/v1/chat/completions", json={**CHAT, "stream": True}) as r:
                next(r.iter_lines())  # accepted; now walk away
        assert backend.cancelled.wait(5)
        with srv.client() as c:  # and the slot is free again
            deadline = time.monotonic() + 5
            while c.get("/health").json()["busy"] and time.monotonic() < deadline:
                time.sleep(0.02)
            assert not c.get("/health").json()["busy"]


def test_backend_error_mid_stream_and_non_stream(live):
    backend = StubBackend(fail_with=BackendError("prompt too long", 400, "context_length_exceeded"))
    with live(backend) as srv, srv.client() as c:
        wait_ready(srv)
        r = c.post("/v1/chat/completions", json=CHAT)
        assert r.status_code == 400
        assert r.json()["error"]["type"] == "context_length_exceeded"
        with c.stream("POST", "/v1/chat/completions", json={**CHAT, "stream": True}) as r:
            _, data = read_sse(r)
        assert data[-2]["error"]["type"] == "context_length_exceeded"
        assert data[-1] == "[DONE]"


def test_api_key_is_enforced(live):
    with live(api_key="sekret") as srv, srv.client() as c:
        wait_ready(srv)
        r = c.get("/v1/models")
        assert r.status_code == 401
        assert r.json()["error"]["type"] == "authentication_error"
        ok = c.get("/v1/models", headers={"Authorization": "Bearer sekret"})
        assert ok.status_code == 200


def test_unknown_route_is_openai_shaped_404(live):
    with live() as srv, srv.client() as c:
        r = c.get("/v1/nothing")
        assert r.status_code == 404
        assert "error" in r.json()


def test_stop_scanner_holds_back_partial_stop():
    s = StopScanner(["END"])
    assert s.feed("hello E") == ("hello", False)
    assert s.feed("N") == (" ", False)
    assert s.feed("D and more") == ("", True)
    s = StopScanner([])
    assert s.feed("abc") == ("abc", False)

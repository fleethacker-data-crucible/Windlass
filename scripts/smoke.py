"""End-to-end smoke test: start Windlass on a small real model and stream a reply.

    python scripts/smoke.py                                   # SmolLM2-135M-Instruct, full precision
    python scripts/smoke.py --compression 4bit                # same, 4-bit layer shards
    python scripts/smoke.py --url http://127.0.0.1:8777/v1    # test a server that is already running

Exits non-zero if anything in the contract Fairlead relies on is missing.
"""

from __future__ import annotations

import argparse
import json
import os
import socket
import subprocess
import sys
import time

import httpx

DEFAULT_MODEL = "HuggingFaceTB/SmolLM2-135M-Instruct"  # Llama architecture, ~270 MB


def free_port() -> int:
    with socket.socket() as s:
        s.bind(("127.0.0.1", 0))
        return s.getsockname()[1]


def fail(message: str) -> None:
    print(f"\nSMOKE FAILED: {message}", file=sys.stderr)
    raise SystemExit(1)


def wait_ready(base: str, proc: subprocess.Popen | None, timeout: float) -> dict:
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        if proc is not None and proc.poll() is not None:
            fail(f"server exited with code {proc.returncode}")
        try:
            health = httpx.get(base.removesuffix("/v1") + "/health", timeout=2).json()
            if health["status"] == "ready":
                return health
            if health["status"] == "error":
                fail(f"model failed to load: {health['error']}")
        except httpx.HTTPError:
            pass
        time.sleep(1)
    fail(f"server not ready after {timeout:.0f}s")


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawTextHelpFormatter)
    ap.add_argument("--model", default=DEFAULT_MODEL)
    ap.add_argument("--compression", choices=["4bit", "8bit"])
    ap.add_argument("--url", help="use a running server instead of starting one")
    ap.add_argument("--prompt", default="Name three things a ship's windlass does. Be brief.")
    ap.add_argument("--max-tokens", type=int, default=48)
    ap.add_argument("--timeout", type=float, default=900, help="seconds to wait for model load")
    args = ap.parse_args()

    proc = None
    log_path = None
    if args.url:
        base = args.url.rstrip("/")
    else:
        port = free_port()
        base = f"http://127.0.0.1:{port}/v1"
        cmd = [sys.executable, "-m", "windlass", "serve", "--model", args.model,
               "--port", str(port), "--keepalive", "5"]
        if args.compression:
            cmd += ["--compression", args.compression]
        log_path = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "smoke-server.log")
        log_file = open(log_path, "w", encoding="utf-8")
        flags = subprocess.CREATE_NO_WINDOW if os.name == "nt" else 0  # no console popping up
        print(f"starting: {' '.join(cmd)}  (log: {os.path.abspath(log_path)})")
        proc = subprocess.Popen(cmd, stdout=log_file, stderr=subprocess.STDOUT,
                                creationflags=flags)
    try:
        wait_ready(base, proc, args.timeout)

        models = httpx.get(base + "/models", timeout=10).json()
        card = models["data"][0]
        if models.get("object") != "list" or not card.get("id") or not card.get("context_length"):
            fail(f"/v1/models is missing id/context_length: {models}")
        print(f"model: {card['id']} (context {card['context_length']})")

        request = {"model": card["id"], "stream": True, "max_tokens": args.max_tokens,
                   "temperature": 0, "stream_options": {"include_usage": True},
                   "messages": [{"role": "user", "content": args.prompt}]}
        started = time.monotonic()
        first_byte = first_token = None
        text, usage, done, finish = "", None, False, None
        print(f"prompt: {args.prompt}\nreply:  ", end="", flush=True)
        with httpx.stream("POST", base + "/chat/completions", json=request,
                          timeout=httpx.Timeout(10, read=120)) as r:
            if r.status_code != 200:
                fail(f"HTTP {r.status_code}: {r.read().decode()}")
            for line in r.iter_lines():
                if first_byte is None:
                    first_byte = time.monotonic() - started
                if not line.startswith("data: "):
                    continue
                payload = line[6:]
                if payload == "[DONE]":
                    done = True
                    break
                chunk = json.loads(payload)
                if "error" in chunk:
                    fail(f"stream error: {chunk['error']}")
                if chunk.get("usage"):
                    usage = chunk["usage"]
                for choice in chunk.get("choices", []):
                    piece = choice["delta"].get("content")
                    if piece:
                        first_token = first_token or time.monotonic() - started
                        text += piece
                        print(piece, end="", flush=True)
                    finish = choice.get("finish_reason") or finish
        elapsed = time.monotonic() - started
        print()
        if not done:
            fail("stream ended without data: [DONE]")
        if not text.strip():
            fail("no tokens streamed")
        if not usage or not usage.get("prompt_tokens") or not usage.get("completion_tokens"):
            fail(f"final usage chunk missing: {usage}")
        print(f"\nOK: {usage['completion_tokens']} tokens in {elapsed:.1f}s "
              f"({usage['completion_tokens'] / elapsed:.2f} tok/s), first byte {first_byte:.2f}s, "
              f"first token {first_token:.2f}s, finish={finish}, "
              f"prompt_tokens={usage['prompt_tokens']}")
        return 0
    finally:
        if proc is not None:
            proc.terminate()
            try:
                proc.wait(10)
            except subprocess.TimeoutExpired:
                proc.kill()


if __name__ == "__main__":
    raise SystemExit(main())

"""Command line: ``windlass serve`` runs the API, ``windlass split`` prepares layer shards."""

from __future__ import annotations

import argparse
import logging
import os
import sys
from pathlib import Path

from . import __version__

DEFAULT_HOST = "127.0.0.1"
DEFAULT_PORT = 8777  # Fleet port registry: next free after Fairlead's 8776


def _add_model_args(p: argparse.ArgumentParser) -> None:
    p.add_argument("--model", required=True,
                   help="Hugging Face repo id, a local checkpoint folder, or a Windlass shards folder")
    p.add_argument("--shards-dir", type=Path,
                   help="where to write per-layer shards (default: $WINDLASS_SHARDS or "
                        "%%LOCALAPPDATA%%\\Windlass\\shards)")
    p.add_argument("--compression", choices=["4bit", "8bit"],
                   help="store layers compressed on disk for faster loading (default: none)")
    p.add_argument("--hf-token", default=os.environ.get("HF_TOKEN"),
                   help="Hugging Face token for gated repos (default: $HF_TOKEN)")
    p.add_argument("--allow-untested-arch", action="store_true",
                   help="try model families other than Llama, Mistral and Qwen")


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(prog="windlass", description=__doc__)
    parser.add_argument("--version", action="version", version=f"windlass {__version__}")
    parser.add_argument("--log-level", default="INFO",
                        choices=["DEBUG", "INFO", "WARNING", "ERROR"])
    sub = parser.add_subparsers(dest="command", required=True)

    serve = sub.add_parser("serve", help="run the OpenAI-compatible server")
    _add_model_args(serve)
    serve.add_argument("--model-id", help="name to publish in /v1/models (default: folder name)")
    serve.add_argument("--host", help=f"address to bind (default: {DEFAULT_HOST})")
    serve.add_argument("--port", type=int, default=DEFAULT_PORT)
    serve.add_argument("--lan", action="store_true",
                       help="bind 0.0.0.0 so other machines (a Fairlead on another PC) can reach it")
    serve.add_argument("--api-key", default=os.environ.get("WINDLASS_API_KEY"),
                       help="require this bearer key on every request (default: $WINDLASS_API_KEY)")
    serve.add_argument("--device", default="auto", help="auto, cuda, cuda:1, mps or cpu")
    serve.add_argument("--dtype", default="auto",
                       choices=["auto", "float16", "bfloat16", "float32"])
    serve.add_argument("--no-prefetch", action="store_true",
                       help="do not read the next layer while the current one computes")
    serve.add_argument("--context-length", type=int,
                       help="override the context length advertised and enforced")
    serve.add_argument("--max-tokens", type=int, default=1024,
                       help="default max_tokens when a request gives none")
    serve.add_argument("--max-queue", type=int, default=8,
                       help="requests that may wait behind the running one before 503")
    serve.add_argument("--keepalive", type=float, default=15.0,
                       help="seconds between SSE keepalive comments (keep under 60)")
    serve.add_argument("--stub", action="store_true",
                       help="serve a canned reply instead of a model (for wiring tests)")

    split = sub.add_parser("split", help="write per-layer shards without serving")
    _add_model_args(split)
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    logging.basicConfig(level=args.log_level,
                        format="%(asctime)s %(levelname)-7s %(name)s: %(message)s")

    if args.command == "split":
        from .engine.shards import default_shards_dir, resolve_checkpoint, split_checkpoint

        checkpoint = resolve_checkpoint(args.model, args.hf_token)
        target = args.shards_dir or default_shards_dir(args.model, args.compression)
        split_checkpoint(checkpoint, target, args.compression, source=args.model)
        print(target)
        return 0

    import uvicorn

    from .backend import StubBackend
    from .server import ServerSettings, create_app

    if args.keepalive >= 60:
        print("--keepalive must be under 60 seconds", file=sys.stderr)
        return 2
    if args.stub:
        backend = StubBackend(model_id=args.model_id or args.model, token_delay=0.2)
    else:
        from .engine.backend import LayeredBackend

        backend = LayeredBackend(
            args.model, model_id=args.model_id, shards_dir=args.shards_dir,
            compression=args.compression, device=args.device, dtype=args.dtype,
            prefetch=not args.no_prefetch, context_length=args.context_length,
            hf_token=args.hf_token, allow_untested_arch=args.allow_untested_arch)

    host = args.host or ("0.0.0.0" if args.lan else DEFAULT_HOST)
    if host not in ("127.0.0.1", "localhost", "::1") and not args.api_key:
        logging.getLogger("windlass").warning(
            "listening on %s without --api-key: anyone on the network can use this model", host)
    settings = ServerSettings(keepalive_interval=args.keepalive, max_queue=args.max_queue,
                              api_key=args.api_key, default_max_tokens=args.max_tokens)
    app = create_app(backend, settings)
    logging.getLogger("windlass").info("Windlass %s on http://%s:%d/v1", __version__, host,
                                       args.port)
    uvicorn.run(app, host=host, port=args.port, log_level=args.log_level.lower(),
                access_log=args.log_level == "DEBUG", timeout_keep_alive=75)
    return 0

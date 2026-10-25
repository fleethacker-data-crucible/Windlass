"""The real backend: a tokenizer, a chat template and a :class:`LayeredModel`."""

from __future__ import annotations

import logging
import threading
import time
from pathlib import Path

import torch
from transformers import AutoTokenizer, GenerationConfig

from ..backend import Backend, BackendError, GenerationRequest, GenerationResult, StopScanner
from .layered import Cancelled, LayeredModel
from .shards import default_shards_dir, read_manifest, resolve_checkpoint, split_checkpoint

log = logging.getLogger("windlass.engine")


def sample(logits: torch.Tensor, temperature: float, top_p: float,
           generator: torch.Generator | None) -> int:
    """Pick the next token from last-position logits of shape [1, vocab]."""
    if temperature <= 0:
        return int(torch.argmax(logits, dim=-1))
    probs = torch.softmax(logits / temperature, dim=-1).squeeze(0)
    if top_p < 1.0:
        sorted_probs, order = torch.sort(probs, descending=True)
        keep = torch.cumsum(sorted_probs, dim=0) - sorted_probs < top_p
        keep[0] = True
        probs = torch.zeros_like(probs).scatter(0, order[keep], sorted_probs[keep])
        probs = probs / probs.sum()
    return int(torch.multinomial(probs.cpu(), 1, generator=generator))


def message_text(content) -> str:
    """OpenAI content is a string or a list of parts; keep the text parts."""
    if content is None:
        return ""
    if isinstance(content, str):
        return content
    if isinstance(content, list):
        parts = []
        for part in content:
            if isinstance(part, dict) and part.get("type") == "text":
                parts.append(str(part.get("text", "")))
            elif isinstance(part, str):
                parts.append(part)
        return "".join(parts)
    return str(content)


class LayeredBackend(Backend):
    def __init__(self, model: str, model_id: str | None = None, shards_dir: Path | None = None,
                 compression: str | None = None, device: str = "auto", dtype: str = "auto",
                 prefetch: bool = True, context_length: int | None = None,
                 hf_token: str | None = None, allow_untested_arch: bool = False):
        self.model_ref = model
        self.model_id = model_id or Path(model.rstrip("/\\")).name
        self.shards_dir = Path(shards_dir) if shards_dir else None
        self.compression = compression
        self.device = device
        self.dtype = dtype
        self.prefetch = prefetch
        self.context_override = context_length
        self.hf_token = hf_token
        self.allow_untested_arch = allow_untested_arch
        self.context_length = context_length or 4096
        self.model: LayeredModel | None = None
        self.tokenizer = None

    # ---- loading ------------------------------------------------------------------------

    def _shards(self) -> Path:
        local = Path(self.model_ref).expanduser()
        if local.is_dir() and read_manifest(local) is not None:
            return local  # already a shards directory
        checkpoint = resolve_checkpoint(self.model_ref, self.hf_token)
        target = self.shards_dir or default_shards_dir(self.model_ref, self.compression)
        split_checkpoint(checkpoint, target, self.compression, source=self.model_ref)
        return target

    def load(self) -> None:
        started = time.monotonic()
        shards = self._shards()
        self.tokenizer = AutoTokenizer.from_pretrained(shards)
        self.model = LayeredModel(shards, device=self.device, dtype=self.dtype,
                                  prefetch=self.prefetch, allow_untested=self.allow_untested_arch)
        self.context_length = self.context_override or self.model.context_length
        try:
            gen = GenerationConfig.from_pretrained(shards)
        except OSError:
            gen = None
        eos = set()
        for source in (getattr(gen, "eos_token_id", None), self.tokenizer.eos_token_id):
            if isinstance(source, int):
                eos.add(source)
            elif isinstance(source, (list, tuple)):
                eos.update(int(x) for x in source)
        self.eos_ids = eos
        self.state = "ready"
        log.info("loaded %s in %.1fs (context %d)", self.model_id,
                 time.monotonic() - started, self.context_length)

    # ---- prompting ----------------------------------------------------------------------

    def encode(self, messages: list[dict]) -> list[int]:
        chat = [{"role": m.get("role", "user"), "content": message_text(m.get("content"))}
                for m in messages]
        if getattr(self.tokenizer, "chat_template", None):
            text = self.tokenizer.apply_chat_template(chat, add_generation_prompt=True,
                                                      tokenize=False)
            return self.tokenizer(text, add_special_tokens=False)["input_ids"]
        text = "".join(f"{m['role']}: {m['content']}\n" for m in chat) + "assistant:"
        return self.tokenizer(text)["input_ids"]

    # ---- generation ---------------------------------------------------------------------

    def generate(self, request: GenerationRequest, on_text, on_layer,
                 cancel: threading.Event) -> GenerationResult:
        assert self.model is not None and self.tokenizer is not None
        prompt = self.encode(request.messages)
        if len(prompt) >= self.context_length:
            raise BackendError(
                f"prompt is {len(prompt)} tokens; {self.model_id} has a {self.context_length}"
                "-token context", 400, "context_length_exceeded")
        max_new = min(request.max_tokens, self.context_length - len(prompt))
        generator = None
        if request.seed is not None:
            generator = torch.Generator().manual_seed(request.seed)

        self.model.on_layer = on_layer
        self.model.cancel = cancel
        cache = self.model.new_cache()
        scanner = StopScanner(request.stop)
        generated: list[int] = []
        sent = ""  # decoded text already handed to the scanner
        ids = torch.tensor([prompt], dtype=torch.long)
        finish = "length"
        try:
            while len(generated) < max_new:
                logits = self.model.step(ids, cache)
                token = sample(logits, request.temperature, request.top_p, generator)
                if token in self.eos_ids:
                    finish = "stop"
                    break
                generated.append(token)
                text = self.tokenizer.decode(generated, skip_special_tokens=True)
                # Wait for the rest of a multi-byte character before sending it.
                if not text.endswith("�") and len(text) > len(sent):
                    piece, stopped = scanner.feed(text[len(sent):])
                    sent = text
                    if piece:
                        on_text(piece)
                    if stopped:
                        finish = "stop"
                        break
                ids = torch.tensor([[token]], dtype=torch.long)
            # Empty after a stop sequence; otherwise the held-back tail of the reply.
            rest = scanner.flush()
            if rest:
                on_text(rest)
        except Cancelled:
            return GenerationResult("cancelled", len(prompt), len(generated))
        finally:
            self.model.on_layer = None
            self.model.cancel = None
            del cache
        return GenerationResult(finish, len(prompt), len(generated))

    def close(self) -> None:
        if self.model is not None:
            self.model.close()

# Adapted from AirLLM (https://github.com/lyogavin/airllm), Apache License 2.0.
# Changed by Windlass contributors: rewritten; see THIRD-PARTY.md.
"""Run a causal LM one layer at a time.

The real transformers model is built on the ``meta`` device, so it costs no memory and still
owns the forward pass: attention, rotary embeddings, masks and the KV cache all come from
transformers and stay correct as new versions land. Windlass attaches hooks to the big
modules (embeddings, every decoder layer, the final norm, the output head). Just before a
module runs, its weights are read from its shard file and placed on the compute device; just
after, they are dropped again. While one layer computes, a worker thread reads the next one
from disk, which is where most of the time goes.

The KV cache is an ordinary transformers ``DynamicCache`` that lives on the compute device
across layers and across decoding steps, so each new token costs one pass over the layer
files rather than a recomputation of the whole prompt.
"""

from __future__ import annotations

import gc
import logging
import threading
import time
from concurrent.futures import Future, ThreadPoolExecutor
from pathlib import Path
from typing import Callable

import torch
from accelerate import init_empty_weights
from accelerate.utils import set_module_tensor_to_device
from transformers import AutoConfig, AutoModelForCausalLM, DynamicCache

from .quant import decompress_state_dict
from .shards import EMBED, LM_HEAD, NORM, ShardError, load_shard, read_manifest

log = logging.getLogger("windlass.engine")

SUPPORTED_MODEL_TYPES = ("llama", "mistral", "qwen2", "qwen3")

# Upper bound on pinned host memory for one prefetched layer. Pinned pages cannot be swapped,
# and two layers are in flight at once, so very large layers fall back to pageable memory.
MAX_PINNED_LAYER_BYTES = 2 * 1024 ** 3


class Cancelled(Exception):
    """Raised inside a forward pass when the caller asked to stop."""


def pick_device(name: str = "auto") -> torch.device:
    if name != "auto":
        return torch.device(name)
    if torch.cuda.is_available():  # also true for ROCm builds
        return torch.device("cuda")
    if getattr(torch.backends, "mps", None) and torch.backends.mps.is_available():
        return torch.device("mps")
    return torch.device("cpu")


def pick_dtype(name: str, device: torch.device) -> torch.dtype:
    if name != "auto":
        return {"float16": torch.float16, "fp16": torch.float16, "bfloat16": torch.bfloat16,
                "bf16": torch.bfloat16, "float32": torch.float32, "fp32": torch.float32}[name]
    if device.type == "cuda":
        return torch.bfloat16 if torch.cuda.is_bf16_supported() else torch.float16
    if device.type == "mps":
        return torch.float16
    return torch.float32


def clean_memory() -> None:
    gc.collect()
    if torch.cuda.is_available():
        torch.cuda.empty_cache()


class LayeredModel:
    """A transformers causal LM whose weights stream from per-layer shard files."""

    def __init__(self, shards_dir: Path, device: torch.device | str = "auto",
                 dtype: torch.dtype | str = "auto", prefetch: bool = True,
                 allow_untested: bool = False):
        self.shards_dir = Path(shards_dir)
        manifest = read_manifest(self.shards_dir)
        if manifest is None:
            raise ShardError(f"{self.shards_dir} is not a Windlass shards directory")
        self.manifest = manifest
        self.layer_names: list[str] = list(manifest["layers"])
        self.tied = bool(manifest["tied_embeddings"])

        self.device = pick_device(device) if isinstance(device, str) else device
        self.dtype = pick_dtype(dtype, self.device) if isinstance(dtype, str) else dtype

        self.config = AutoConfig.from_pretrained(self.shards_dir)
        if self.config.model_type not in SUPPORTED_MODEL_TYPES:
            msg = (f"model type {self.config.model_type!r} is not one Windlass is tested with "
                   f"({', '.join(SUPPORTED_MODEL_TYPES)})")
            if not allow_untested:
                raise ShardError(msg + "; pass --allow-untested-arch to try it anyway")
            log.warning(msg)
        with init_empty_weights():
            # Buffers such as the rotary inv_freq are still created for real (include_buffers
            # defaults to False), so only the parameters live on meta.
            self.model = AutoModelForCausalLM.from_config(self.config, dtype=self.dtype)
        self.model.eval()
        # Small buffers (rotary tables) must sit with the computation.
        for module in self.model.modules():
            for key, buf in list(module._buffers.items()):
                if buf is not None and buf.device.type != "meta":
                    module._buffers[key] = buf.to(self.device)

        self._placed: dict[str, list[str]] = {}
        self.modules = self._find_modules()
        self.streamed = list(self.layer_names)
        if self.tied:
            # The embedding is also the output head: keep it resident and stream the rest.
            self._place(EMBED, self._read(EMBED))
            self.model.tie_weights()
            self.streamed.remove(EMBED)
        self._position = {name: i for i, name in enumerate(self.streamed)}

        self.prefetch = prefetch
        self._executor = ThreadPoolExecutor(max_workers=1, thread_name_prefix="windlass-prefetch")
        self._pending: tuple[str, Future] | None = None
        self._lock = threading.Lock()
        self.on_layer: Callable[[int, int], None] | None = None
        self.cancel: threading.Event | None = None

        for name in self.streamed:
            module = self.modules[name]
            module.register_forward_pre_hook(self._make_pre_hook(name))
            module.register_forward_hook(self._make_post_hook(name))
        log.info("layered model ready: %s, %d streamed modules, device=%s dtype=%s compression=%s",
                 self.config.model_type, len(self.streamed), self.device, self.dtype,
                 manifest.get("compression") or "none")

    # ---- module lookup ------------------------------------------------------------------

    def _find_modules(self) -> dict[str, torch.nn.Module]:
        named = dict(self.model.named_modules())
        found = {}
        for name in self.layer_names:
            if name not in named:
                raise ShardError(f"model has no module {name!r}; the shards do not match it")
            found[name] = named[name]
        if self.tied and LM_HEAD not in found:
            found[LM_HEAD] = named[LM_HEAD]
        return found

    @property
    def context_length(self) -> int:
        return int(getattr(self.config, "max_position_embeddings", 4096))

    # ---- weight streaming ---------------------------------------------------------------

    def _read(self, name: str) -> dict[str, torch.Tensor]:
        state = load_shard(self.shards_dir, name)
        if self.device.type == "cuda":
            size = sum(t.numel() * t.element_size() for t in state.values())
            if size <= MAX_PINNED_LAYER_BYTES:
                try:
                    state = {k: v.pin_memory() for k, v in state.items()}
                except RuntimeError:
                    pass  # out of pinned memory; pageable is slower but works
        return state

    def _place(self, name: str, state: dict[str, torch.Tensor]) -> None:
        state = decompress_state_dict(state, self.dtype, self.device)
        for key, value in state.items():
            set_module_tensor_to_device(self.model, key, self.device, value=value)
        self._placed[name] = list(state)

    def _evict(self, name: str) -> None:
        for key in self._placed.pop(name, ()):
            set_module_tensor_to_device(self.model, key, "meta")

    def _next(self, name: str) -> str:
        # Wrap around: after the output head the next step starts with the embeddings again.
        return self.streamed[(self._position[name] + 1) % len(self.streamed)]

    def _make_pre_hook(self, name: str):
        def hook(module, args):
            if self.cancel is not None and self.cancel.is_set():
                raise Cancelled()
            pending = self._pending
            if pending is not None and pending[0] == name:
                state = pending[1].result()
            else:
                state = self._read(name)
            self._pending = None
            self._place(name, state)
            if self.prefetch:
                nxt = self._next(name)
                self._pending = (nxt, self._executor.submit(self._read, nxt))
            if self.on_layer is not None:
                self.on_layer(self._position[name] + 1, len(self.streamed))
        return hook

    def _make_post_hook(self, name: str):
        def hook(module, args, output):
            self._evict(name)
            return output
        return hook

    def _evict_all(self) -> None:
        for name in list(self._placed):
            if name in self._position:
                self._evict(name)

    # ---- inference ----------------------------------------------------------------------

    def new_cache(self) -> DynamicCache:
        return DynamicCache()

    @torch.inference_mode()
    def step(self, input_ids: torch.Tensor, cache: DynamicCache) -> torch.Tensor:
        """Run one pass over every layer and return the logits for the last position."""
        with self._lock:
            try:
                out = self.model(input_ids=input_ids.to(self.device), past_key_values=cache,
                                 use_cache=True, logits_to_keep=1)
                return out.logits[:, -1, :].float()
            except BaseException:
                self._evict_all()
                pending, self._pending = self._pending, None
                if pending is not None:
                    pending[1].cancel()
                raise

    def close(self) -> None:
        self._executor.shutdown(wait=False, cancel_futures=True)
        clean_memory()

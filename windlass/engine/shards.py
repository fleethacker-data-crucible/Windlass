# Adapted from AirLLM (https://github.com/lyogavin/airllm), Apache License 2.0.
# Changed by Windlass contributors: rewritten; see THIRD-PARTY.md.
"""Split a Hugging Face checkpoint into one safetensors file per layer.

A layered model reads one layer at a time, so the checkpoint is rewritten once into:

    <shards_dir>/
        windlass.json                     manifest (layer order, compression, source)
        config.json, tokenizer files ...  copied from the source checkpoint
        model.embed_tokens.safetensors
        model.layers.0.safetensors ... model.layers.N.safetensors
        model.norm.safetensors
        lm_head.safetensors               absent when embeddings are tied

Splitting streams one layer through memory at a time (safetensors files are memory-mapped),
so a 70B checkpoint can be split on a machine with far less RAM than the model.
"""

from __future__ import annotations

import hashlib
import json
import logging
import os
import re
import shutil
from pathlib import Path

from safetensors import safe_open
from safetensors.torch import load_file, save_file

from .quant import COMPRESSIONS, compress_state_dict

log = logging.getLogger("windlass.shards")

MANIFEST = "windlass.json"
FORMAT_VERSION = 1

EMBED = "model.embed_tokens"
NORM = "model.norm"
LM_HEAD = "lm_head"
LAYER_PREFIX = "model.layers"

# Files worth carrying over next to the shards so the shards directory is self-contained.
_SIDECAR_FILES = (
    "config.json", "generation_config.json", "tokenizer.json", "tokenizer_config.json",
    "tokenizer.model", "special_tokens_map.json", "vocab.json", "merges.txt",
    "added_tokens.json", "chat_template.jinja", "chat_template.json",
)

_LAYER_RE = re.compile(r"^model\.layers\.(\d+)\.")


class ShardError(RuntimeError):
    pass


def resolve_checkpoint(model: str, token: str | None = None) -> Path:
    """Return a local directory for ``model``: a path as-is, or a Hub repo id downloaded."""
    path = Path(model).expanduser()
    if path.is_dir():
        return path
    from huggingface_hub import snapshot_download

    log.info("downloading %s from the Hugging Face Hub", model)
    return Path(snapshot_download(
        model, token=token,
        allow_patterns=["*.safetensors", "*.json", "*.model", "*.txt", "*.jinja"],
    ))


def default_shards_dir(model: str, compression: str | None) -> Path:
    """Where shards go when ``--shards-dir`` is not given.

    ``$WINDLASS_SHARDS`` if set, else ``%LOCALAPPDATA%\Windlass\shards`` on Windows and
    ``~/.cache/windlass/shards`` elsewhere; one folder per model and compression.
    """
    root = os.environ.get("WINDLASS_SHARDS")
    if root:
        base = Path(root)
    elif os.name == "nt" and os.environ.get("LOCALAPPDATA"):
        base = Path(os.environ["LOCALAPPDATA"]) / "Windlass" / "shards"
    else:
        base = Path.home() / ".cache" / "windlass" / "shards"
    path = Path(model).expanduser()
    if path.is_dir():
        # Two local folders with the same name must not share shards.
        digest = hashlib.sha1(str(path.resolve()).encode("utf-8")).hexdigest()[:8]
        name = f"{path.resolve().name}-{digest}"
    else:
        name = model.replace("/", "--")
    return base / name / (compression or "full")


def owner_of(key: str) -> str | None:
    """Which layer file a checkpoint tensor belongs in, or None if it is not needed."""
    m = _LAYER_RE.match(key)
    if m:
        return f"{LAYER_PREFIX}.{m.group(1)}"
    for name in (EMBED, NORM, LM_HEAD):
        if key == name or key.startswith(name + "."):
            return name
    return None


def _tensor_locations(checkpoint: Path) -> dict[str, Path]:
    index = checkpoint / "model.safetensors.index.json"
    if index.is_file():
        weight_map = json.loads(index.read_text(encoding="utf-8"))["weight_map"]
        return {k: checkpoint / v for k, v in weight_map.items()}
    files = sorted(checkpoint.glob("*.safetensors"))
    if not files:
        raise ShardError(
            f"{checkpoint} has no .safetensors weights. Windlass reads safetensors checkpoints "
            "only; convert a .bin checkpoint with transformers' save_pretrained first.")
    locations: dict[str, Path] = {}
    for f in files:
        with safe_open(str(f), framework="pt") as st:
            for k in st.keys():
                locations[k] = f
    return locations


def read_manifest(shards_dir: Path) -> dict | None:
    path = Path(shards_dir) / MANIFEST
    if not path.is_file():
        return None
    return json.loads(path.read_text(encoding="utf-8"))


def split_checkpoint(checkpoint: Path, shards_dir: Path, compression: str | None = None,
                     source: str | None = None) -> dict:
    """Write per-layer shards for ``checkpoint`` into ``shards_dir`` and return the manifest.

    Reuses an existing split when its manifest matches, so restarts are quick.
    """
    if compression is not None and compression not in COMPRESSIONS:
        raise ShardError(f"unknown compression {compression!r}; use one of {COMPRESSIONS}")
    checkpoint = Path(checkpoint)
    shards_dir = Path(shards_dir)

    existing = read_manifest(shards_dir)
    if (existing and existing.get("format_version") == FORMAT_VERSION
            and existing.get("compression") == compression
            and all((shards_dir / f"{name}.safetensors").is_file() for name in existing["layers"])):
        log.info("reusing layer shards in %s", shards_dir)
        return existing

    config = json.loads((checkpoint / "config.json").read_text(encoding="utf-8"))
    n_layers = int(config["num_hidden_layers"])

    locations = _tensor_locations(checkpoint)
    groups: dict[str, list[str]] = {}
    skipped = []
    for key in locations:
        owner = owner_of(key)
        if owner is None:
            skipped.append(key)
        else:
            groups.setdefault(owner, []).append(key)
    if skipped:
        log.warning("ignoring %d tensors outside embed/layers/norm/lm_head (e.g. %s)",
                    len(skipped), skipped[0])

    order = [EMBED] + [f"{LAYER_PREFIX}.{i}" for i in range(n_layers)] + [NORM]
    # Tied models reuse the embedding matrix as the output head, so no lm_head file is needed.
    tied = bool(config.get("tie_word_embeddings", False)) or LM_HEAD not in groups
    if not tied:
        order.append(LM_HEAD)
    missing = [name for name in order if name not in groups]
    if missing:
        raise ShardError(f"checkpoint is missing weights for {', '.join(missing[:5])}")

    shards_dir.mkdir(parents=True, exist_ok=True)
    (shards_dir / MANIFEST).unlink(missing_ok=True)  # an interrupted split is never reused

    for i, name in enumerate(order):
        state = {}
        by_file: dict[Path, list[str]] = {}
        for key in groups[name]:
            by_file.setdefault(locations[key], []).append(key)
        for f, keys in by_file.items():
            with safe_open(str(f), framework="pt") as st:
                for key in keys:
                    state[key] = st.get_tensor(key)
        state = compress_state_dict(state, compression)
        tmp = shards_dir / f"{name}.safetensors.tmp"
        save_file({k: v.contiguous() for k, v in state.items()}, str(tmp))
        os.replace(tmp, shards_dir / f"{name}.safetensors")
        log.info("split %s (%d/%d)", name, i + 1, len(order))

    for sidecar in _SIDECAR_FILES:
        src = checkpoint / sidecar
        if src.is_file():
            shutil.copy2(src, shards_dir / sidecar)

    manifest = {
        "format_version": FORMAT_VERSION,
        "source": source or str(checkpoint),
        "model_type": config.get("model_type"),
        "compression": compression,
        "tied_embeddings": tied,
        "layers": order,
    }
    (shards_dir / MANIFEST).write_text(json.dumps(manifest, indent=2), encoding="utf-8")
    return manifest


def load_shard(shards_dir: Path, name: str) -> dict:
    return load_file(str(Path(shards_dir) / f"{name}.safetensors"), device="cpu")

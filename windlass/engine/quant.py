# Adapted from AirLLM (https://github.com/lyogavin/airllm), Apache License 2.0.
# Changed by Windlass contributors: rewritten; see THIRD-PARTY.md.
"""Blockwise 8-bit and 4-bit weight compression in plain PyTorch.

The point of compression in Windlass is the same as in AirLLM: a layered model spends most
of its time reading layer files from disk, so smaller files mean faster tokens. Weights are
stored compressed on disk and expanded back to the runtime dtype on the compute device just
before the layer runs, so the maths itself stays in fp16/bf16/fp32.

AirLLM uses bitsandbytes for this, which needs CUDA. This module does the same job in
ordinary tensor ops so it also works on CPU, ROCm and Apple GPUs:

* ``8bit``: symmetric absmax int8 per block of 64 values (about 0.53x the fp16 size).
* ``4bit``: NF4 (the 16-level normal-float code from QLoRA) per block of 64 values, two
  codes packed per byte (about 0.28x the fp16 size).

A compressed tensor ``name`` is stored as three entries: ``name`` (the int8/uint8 payload),
``name.__absmax`` (fp16 scale per block) and ``name.__shape`` (int64 original shape).
"""

from __future__ import annotations

import torch

BLOCK = 64
ABSMAX_SUFFIX = ".__absmax"
SHAPE_SUFFIX = ".__shape"
COMPRESSIONS = ("8bit", "4bit")

# NF4 code values (QLoRA, Dettmers et al. 2023), ascending.
_NF4 = torch.tensor([
    -1.0, -0.6961928009986877, -0.5250730514526367, -0.39491748809814453,
    -0.28444138169288635, -0.18477343022823334, -0.09105003625154495, 0.0,
    0.07958029955625534, 0.16093020141124725, 0.24611230194568634, 0.33791524171829224,
    0.44070982933044434, 0.5626170039176941, 0.7229568362236023, 1.0,
])
_NF4_MIDPOINTS = (_NF4[1:] + _NF4[:-1]) / 2


def should_compress(name: str, tensor: torch.Tensor) -> bool:
    """Compress the big matrices only. Norms, biases and embeddings stay exact."""
    return (
        tensor.dim() == 2
        and tensor.is_floating_point()
        and tensor.numel() >= BLOCK * 16
        and "embed" not in name
    )


def _blocks(tensor: torch.Tensor) -> tuple[torch.Tensor, int]:
    flat = tensor.detach().reshape(-1).float()
    pad = (-flat.numel()) % BLOCK
    if pad:
        flat = torch.cat([flat, flat.new_zeros(pad)])
    return flat.view(-1, BLOCK), pad


def compress_tensor(name: str, tensor: torch.Tensor, mode: str) -> dict[str, torch.Tensor]:
    if mode not in COMPRESSIONS:
        raise ValueError(f"unknown compression {mode!r}; use one of {COMPRESSIONS}")
    blocks, _ = _blocks(tensor)
    absmax = blocks.abs().amax(dim=1, keepdim=True).clamp_min(1e-8)
    scaled = blocks / absmax
    if mode == "8bit":
        payload = torch.round(scaled * 127).clamp(-127, 127).to(torch.int8).reshape(-1)
    else:
        codes = torch.bucketize(scaled.reshape(-1), _NF4_MIDPOINTS).to(torch.uint8)
        payload = (codes[0::2] << 4) | codes[1::2]
    return {
        name: payload.contiguous(),
        name + ABSMAX_SUFFIX: absmax.reshape(-1).to(torch.float16).contiguous(),
        name + SHAPE_SUFFIX: torch.tensor(list(tensor.shape), dtype=torch.int64),
    }


def decompress_tensor(payload: torch.Tensor, absmax: torch.Tensor, shape: torch.Tensor,
                      dtype: torch.dtype) -> torch.Tensor:
    """Expand one compressed tensor on whatever device ``payload`` lives on."""
    dims = [int(d) for d in shape.tolist()]
    numel = 1
    for d in dims:
        numel *= d
    if payload.dtype == torch.int8:
        values = payload.float()
        scale = 127.0
    else:
        nf4 = _NF4.to(payload.device)
        high = (payload >> 4).long()
        low = (payload & 0x0F).long()
        values = torch.stack([nf4[high], nf4[low]], dim=1).reshape(-1)
        scale = 1.0
    blocks = values.view(-1, BLOCK) * (absmax.float().view(-1, 1) / scale)
    return blocks.reshape(-1)[:numel].view(dims).to(dtype)


def compress_state_dict(state: dict[str, torch.Tensor], mode: str | None) -> dict[str, torch.Tensor]:
    if not mode:
        return state
    out: dict[str, torch.Tensor] = {}
    for name, tensor in state.items():
        if should_compress(name, tensor):
            out.update(compress_tensor(name, tensor, mode))
        else:
            out[name] = tensor
    return out


def decompress_state_dict(state: dict[str, torch.Tensor], dtype: torch.dtype,
                          device: torch.device | str) -> dict[str, torch.Tensor]:
    """Move a (possibly compressed) layer to ``device`` and expand it to ``dtype``."""
    out: dict[str, torch.Tensor] = {}
    for name, tensor in state.items():
        if name.endswith(ABSMAX_SUFFIX) or name.endswith(SHAPE_SUFFIX):
            continue
        absmax = state.get(name + ABSMAX_SUFFIX)
        if absmax is None:
            out[name] = tensor.to(device, non_blocking=True)
            if out[name].is_floating_point():
                out[name] = out[name].to(dtype)
        else:
            out[name] = decompress_tensor(
                tensor.to(device, non_blocking=True), absmax.to(device, non_blocking=True),
                state[name + SHAPE_SUFFIX], dtype)
    return out

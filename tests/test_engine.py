"""Layered inference must match the ordinary in-memory model. Uses tiny random models."""

import threading

import pytest

torch = pytest.importorskip("torch")
transformers = pytest.importorskip("transformers")

from tokenizers import Tokenizer, models, pre_tokenizers  # noqa: E402
from transformers import (  # noqa: E402
    AutoModelForCausalLM, LlamaConfig, MistralConfig, PreTrainedTokenizerFast, Qwen2Config,
    Qwen3Config)

from windlass.backend import GenerationRequest  # noqa: E402
from windlass.engine.backend import LayeredBackend  # noqa: E402
from windlass.engine.layered import Cancelled, LayeredModel  # noqa: E402
from windlass.engine.quant import compress_tensor, decompress_tensor  # noqa: E402
from windlass.engine.shards import read_manifest, split_checkpoint  # noqa: E402

TINY = dict(vocab_size=96, hidden_size=64, intermediate_size=128, num_hidden_layers=3,
            num_attention_heads=4, num_key_value_heads=2, max_position_embeddings=256)

FAMILIES = {
    "llama": lambda tie: LlamaConfig(**TINY, tie_word_embeddings=tie),
    "mistral": lambda tie: MistralConfig(**TINY, tie_word_embeddings=tie),
    "qwen2": lambda tie: Qwen2Config(**TINY, tie_word_embeddings=tie),
    "qwen3": lambda tie: Qwen3Config(**TINY, head_dim=16, tie_word_embeddings=tie),
}


def tiny_checkpoint(tmp_path, family="llama", tie=False, seed=0):
    torch.manual_seed(seed)
    model = AutoModelForCausalLM.from_config(FAMILIES[family](tie)).eval()
    path = tmp_path / f"{family}-{tie}"
    model.save_pretrained(path)
    return model, path


@pytest.mark.parametrize("family", sorted(FAMILIES))
@pytest.mark.parametrize("tie", [False, True])
def test_layered_matches_full_model_with_kv_cache(tmp_path, family, tie):
    model, path = tiny_checkpoint(tmp_path, family, tie)
    manifest = split_checkpoint(path, tmp_path / "shards")
    assert manifest["tied_embeddings"] == tie
    assert ("lm_head" in manifest["layers"]) != tie
    layered = LayeredModel(tmp_path / "shards", device="cpu", dtype="float32")
    ids = torch.tensor([[1, 5, 9, 22, 7]])
    with torch.no_grad():
        cache = layered.new_cache()
        prefill = layered.step(ids, cache)
        torch.testing.assert_close(prefill, model(ids).logits[:, -1], atol=1e-5, rtol=1e-4)
        # The next step only feeds the new token; the cache carries the rest.
        decode = layered.step(torch.tensor([[3]]), cache)
        full = model(torch.cat([ids, torch.tensor([[3]])], 1)).logits[:, -1]
        torch.testing.assert_close(decode, full, atol=1e-5, rtol=1e-4)
    # Nothing but the resident embedding stays materialised between passes.
    on_meta = {n for n, p in layered.model.named_parameters() if p.device.type == "meta"}
    assert any(n.startswith("model.layers.0.") for n in on_meta)
    layered.close()


def test_split_is_reused_and_interrupted_split_is_not(tmp_path):
    _, path = tiny_checkpoint(tmp_path)
    shards = tmp_path / "shards"
    split_checkpoint(path, shards)
    stamp = (shards / "model.layers.0.safetensors").stat().st_mtime_ns
    split_checkpoint(path, shards)
    assert (shards / "model.layers.0.safetensors").stat().st_mtime_ns == stamp
    (shards / "model.layers.1.safetensors").unlink()
    split_checkpoint(path, shards)
    assert (shards / "model.layers.1.safetensors").is_file()
    assert read_manifest(shards)["compression"] is None


@pytest.mark.parametrize("mode,tolerance", [("8bit", 0.01), ("4bit", 0.12)])
def test_compression_round_trip(mode, tolerance):
    torch.manual_seed(0)
    w = torch.randn(130, 70)  # not a multiple of the block size
    packed = compress_tensor("w", w, mode)
    back = decompress_tensor(packed["w"], packed["w.__absmax"], packed["w.__shape"],
                             torch.float32)
    assert back.shape == w.shape
    rel = (back - w).norm() / w.norm()
    assert rel < tolerance


@pytest.mark.parametrize("mode", ["8bit", "4bit"])
def test_compressed_layers_stay_close(tmp_path, mode):
    model, path = tiny_checkpoint(tmp_path, "llama")
    split_checkpoint(path, tmp_path / mode, compression=mode)
    full_size = sum(f.stat().st_size for f in path.glob("*.safetensors"))
    shard_size = sum(f.stat().st_size for f in (tmp_path / mode).glob("*.safetensors"))
    assert shard_size < full_size
    layered = LayeredModel(tmp_path / mode, device="cpu", dtype="float32")
    ids = torch.tensor([[4, 8, 15, 16, 23, 42]])
    with torch.no_grad():
        got = torch.softmax(layered.step(ids, layered.new_cache()), -1)
        want = torch.softmax(model(ids).logits[:, -1], -1)
    assert (got - want).abs().max() < (0.02 if mode == "8bit" else 0.1)


def test_cancel_stops_mid_pass_and_frees_layers(tmp_path):
    _, path = tiny_checkpoint(tmp_path)
    split_checkpoint(path, tmp_path / "shards")
    layered = LayeredModel(tmp_path / "shards", device="cpu", dtype="float32")
    cancel = threading.Event()
    layered.cancel = cancel
    layered.on_layer = lambda i, n: cancel.set() if i == 2 else None
    with pytest.raises(Cancelled):
        layered.step(torch.tensor([[1, 2, 3]]), layered.new_cache())
    assert all(p.device.type == "meta" for p in layered.model.parameters())
    # And it works again afterwards.
    layered.cancel = None
    layered.on_layer = None
    assert layered.step(torch.tensor([[1, 2, 3]]), layered.new_cache()).shape == (1, 96)


def word_tokenizer(path):
    words = ["<unk>", "<eos>", "user", "assistant", ":", "hello", "windlass"] + \
        [f"w{i}" for i in range(89)]
    tok = Tokenizer(models.WordLevel({w: i for i, w in enumerate(words)}, unk_token="<unk>"))
    tok.pre_tokenizer = pre_tokenizers.WhitespaceSplit()
    fast = PreTrainedTokenizerFast(tokenizer_object=tok, unk_token="<unk>", eos_token="<eos>")
    fast.chat_template = ("{% for m in messages %}{{ m['role'] }} : {{ m['content'] }} "
                          "{% endfor %}{% if add_generation_prompt %}assistant : {% endif %}")
    fast.save_pretrained(path)
    return fast


def test_backend_greedy_matches_transformers_generate(tmp_path):
    model, path = tiny_checkpoint(tmp_path, "llama", seed=3)
    tok = word_tokenizer(path)
    backend = LayeredBackend(str(path), model_id="tiny", shards_dir=tmp_path / "shards",
                             device="cpu", dtype="float32")
    backend.load()
    assert backend.context_length == 256

    messages = [{"role": "user", "content": "hello windlass"}]
    prompt = backend.encode(messages)
    assert prompt == tok("user : hello windlass assistant :")["input_ids"]
    with torch.no_grad():
        expected = model.generate(torch.tensor([prompt]), max_new_tokens=8, do_sample=False,
                                  eos_token_id=None, pad_token_id=0)[0, len(prompt):].tolist()
    if 1 in expected:  # the backend stops at <eos>
        expected = expected[:expected.index(1)]

    pieces, layers = [], []
    result = backend.generate(
        GenerationRequest(messages=messages, max_tokens=8, temperature=0),
        pieces.append, lambda i, n: layers.append((i, n)), threading.Event())
    assert result.prompt_tokens == len(prompt)
    assert result.completion_tokens == len(expected)
    assert "".join(pieces) == tok.decode(expected, skip_special_tokens=True)
    assert layers[-1] == (6, 6)  # embed + 3 decoder layers + norm + lm_head
    backend.close()

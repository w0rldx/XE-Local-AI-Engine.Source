"""Self-checks for modellab.py's attention capture on real torch.

Run: ``python3 tools/training/test_modellab_attention.py``.

Assert-based with no framework, like test_modellab_lens.py. Only transformers is stubbed; torch is the real package (the
CPU wheel in the root dev venv). The attention modules are hand-written stand-ins of no transformers class, resolving
the "sdpa" entry of a stub registry per call, as transformers' modules do; that entry runs torch's own SDPA. Where torch
is not installed, every test skips with the reason.
"""

import contextlib
import json
import os
import tempfile
import types
from unittest import mock

from test_stubs import load_modellab, require_torch, run_standalone

HIDDEN = 16
HEAD_DIM = 4
VOCAB = 11
BLOCKS = 4
CHOSEN = [1, 3]
TOP_KEYS = 3
PROMPT = [[1, 2, 3, 4, 5]]


def _sdpa(module, query, key, value, attention_mask, scaling=None, **_kwargs):
    """transformers' sdpa entry in miniature: KV heads repeated, causal only over a multi-token query."""
    torch = require_torch()
    groups = query.shape[1] // key.shape[1]
    output = torch.nn.functional.scaled_dot_product_attention(
        query,
        key.repeat_interleave(groups, dim=1),
        value.repeat_interleave(groups, dim=1),
        attn_mask=attention_mask,
        is_causal=query.shape[2] > 1,
        scale=scaling,
    )
    return output.transpose(1, 2), None


def _model(registry, heads=4, kv_heads=4):
    """BLOCKS blocks under model.model.layers, each with a self_attn whose layer_idx is not its block index, and a
    vision stand-in of the same class carrying block 1's layer_idx, outside every block. Each attention module keeps
    its own key/value history, so a decode forward passes only the newest token."""
    torch = require_torch()
    nn = torch.nn
    torch.manual_seed(0)

    class Attention(nn.Module):
        def __init__(self, layer_idx):
            super().__init__()
            self.head_dim, self.scaling, self.layer_idx = HEAD_DIM, HEAD_DIM**-0.5, layer_idx
            self.heads, self.kv_heads = heads, kv_heads
            self.q_proj = nn.Linear(HIDDEN, heads * HEAD_DIM, bias=False)
            self.k_proj = nn.Linear(HIDDEN, kv_heads * HEAD_DIM, bias=False)
            self.v_proj = nn.Linear(HIDDEN, kv_heads * HEAD_DIM, bias=False)
            self.o_proj = nn.Linear(heads * HEAD_DIM, HIDDEN, bias=False)
            self.history = None

        def forward(self, hidden):
            tokens = hidden.shape[1]
            query = self.q_proj(hidden).view(1, tokens, self.heads, HEAD_DIM).transpose(1, 2)
            key = self.k_proj(hidden).view(1, tokens, self.kv_heads, HEAD_DIM).transpose(1, 2)
            value = self.v_proj(hidden).view(1, tokens, self.kv_heads, HEAD_DIM).transpose(1, 2)
            if self.history is not None:
                key, value = torch.cat([self.history[0], key], 2), torch.cat([self.history[1], value], 2)
            self.history = (key, value)
            # Looked up per call, as transformers does: the capture swaps this entry.
            output, _ = registry["sdpa"](self, query, key, value, None, scaling=self.scaling)
            return self.o_proj(output.reshape(1, tokens, -1))

    class Block(nn.Module):
        def __init__(self, index):
            super().__init__()
            self.self_attn = Attention(10 + index)

        def forward(self, hidden):
            return hidden + self.self_attn(hidden)

    class Inner(nn.Module):
        def __init__(self):
            super().__init__()
            self.embed = nn.Embedding(VOCAB, HIDDEN)
            self.layers = nn.ModuleList(Block(i) for i in range(BLOCKS))
            self.vision = Attention(11)

    class Model(nn.Module):
        def __init__(self):
            super().__init__()
            self.model = Inner()
            self.head = nn.Linear(HIDDEN, VOCAB)

        def forward(self, ids):
            hidden = self.model.embed(ids)
            self.model.vision(hidden)
            for block in self.model.layers:
                hidden = block(hidden)
            return self.head(hidden)

    return Model().eval()


def _generate(model, steps):
    """Greedy decoding: the prompt in one forward, then one token a forward. Returns every step's logits."""
    torch = require_torch()
    feed, logits = torch.tensor(PROMPT), []
    with torch.inference_mode():
        for _ in range(steps):
            logits.append(model(feed)[0, -1])
            feed = torch.argmax(logits[-1]).view(1, 1)
    return torch.stack(logits)


def _hooks(model):
    return sum(len(m._forward_hooks) + len(m._forward_pre_hooks) for m in model.modules())


def _captured(modellab, heads, kv_heads, steps=3):
    """(rows, the kernel's (module, query, key) per call, head_dim, model) for a capture of CHOSEN."""
    calls = []

    def kernel(module, query, key, value, attention_mask, **kwargs):
        calls.append((module, query, key))
        return _sdpa(module, query, key, value, attention_mask, **kwargs)

    registry = {"sdpa": kernel}
    model = _model(registry, heads, kv_heads)
    with (
        mock.patch.object(modellab, "_attention_functions", return_value=registry),
        contextlib.ExitStack() as stack,
    ):
        attention = modellab._AttentionRows(stack, model.model.layers, CHOSEN, len(PROMPT[0]), TOP_KEYS)
        _generate(model, steps)
    assert registry["sdpa"] is kernel
    return attention, calls, model


def test_rows_are_an_independent_softmax_of_the_kernels_own_inputs_with_and_without_grouped_heads():
    torch = require_torch()
    modellab = load_modellab()
    for heads, kv_heads in ((4, 4), (4, 2)):
        attention, calls, model = _captured(modellab, heads, kv_heads)
        assert len(attention.rows) == 3 * len(CHOSEN)
        for step, layer, position, keys, weights in attention.rows:
            module = model.model.layers[layer].self_attn
            (_, query, key) = [call for call in calls if call[0] is module][step]
            assert position == len(PROMPT[0]) + step - 1 == key.shape[2] - 1
            expanded = key[0].repeat_interleave(heads // kv_heads, dim=0)
            expected = torch.softmax(query[0, :, -1:] @ expanded.transpose(1, 2) * HEAD_DIM**-0.5, -1)[:, 0]
            top = expected.topk(min(TOP_KEYS, key.shape[2]), -1)
            assert keys.dtype == torch.int32
            assert torch.equal(keys, top.indices.int()), (heads, kv_heads, step, layer)
            assert torch.allclose(weights, top.values, atol=1e-6)


def test_only_the_chosen_blocks_modules_are_captured_under_their_block_index():
    modellab = load_modellab()
    attention, calls, model = _captured(modellab, 4, 2)
    vision = model.model.vision
    # The stand-in ran beside the selected module with that module's layer_idx, and gave no row.
    assert vision.layer_idx == model.model.layers[1].self_attn.layer_idx
    assert any(call[0] is vision for call in calls)
    assert sorted((step, layer) for step, layer, *_ in attention.rows) == [(s, b) for s in range(3) for b in CHOSEN]
    assert attention.head_dim == HEAD_DIM


def test_the_kernels_output_is_unchanged():
    torch = require_torch()
    modellab = load_modellab()
    registry = {"sdpa": _sdpa}
    off = _generate(_model(registry), 4)
    model = _model(registry)
    with (
        mock.patch.object(modellab, "_attention_functions", return_value=registry),
        contextlib.ExitStack() as stack,
    ):
        modellab._AttentionRows(stack, model.model.layers, CHOSEN, len(PROMPT[0]), TOP_KEYS)
        on = _generate(model, 4)
    assert torch.equal(on, off)


def test_a_failed_step_check_restores_the_kernel():
    modellab = load_modellab()
    registry = {"sdpa": _sdpa}
    model = _model(registry)
    try:
        with (
            mock.patch.object(modellab, "_attention_functions", return_value=registry),
            contextlib.ExitStack() as stack,
        ):
            # One prompt token too many: the first capture sees fewer keys than the check expects.
            modellab._AttentionRows(stack, model.model.layers, CHOSEN, len(PROMPT[0]) + 1, TOP_KEYS)
            _generate(model, 1)
    except modellab.JobError as error:
        assert "Block 1 saw 5 keys at step 0" in str(error), str(error)
    else:
        raise AssertionError("A capture at the wrong position must be refused.")
    assert registry["sdpa"] is _sdpa


def _trace_until_prompt(modellab, model, model_type, attention_layers, **text_fields):
    """Runs trace() through its admission checks: a JobError, or "admitted" once it reaches the prompt."""
    registry = {"sdpa": _sdpa}
    text_config = types.SimpleNamespace(**text_fields)
    model.config = types.SimpleNamespace(
        model_type=model_type, _attn_implementation="sdpa", get_text_config=lambda decoder=False: text_config
    )
    modellab._SLOT.loaded, modellab._SLOT.key = (None, model), "k"
    job = {
        "modelKey": "k",
        "layers": [0],
        "attentionPath": "unused",
        "prompt": "p",
        "attentionLayers": attention_layers,
        "attentionTopKeys": TOP_KEYS,
        "attentionByteCap": 1_000,
    }
    reached = RuntimeError("admitted")
    with (
        mock.patch.object(modellab, "_attention_functions", return_value=registry),
        mock.patch.object(modellab, "prompt_ids", side_effect=reached),
    ):
        try:
            modellab.trace(job, modellab._Run("j"))
        except modellab.JobError as error:
            outcome = str(error)
        except RuntimeError as error:
            assert error is reached
            outcome = "admitted"
        else:
            raise AssertionError("trace() must stop at a refusal or at the prompt.")
    assert _hooks(model) == 0
    assert registry["sdpa"] is _sdpa
    return outcome


def test_attention_outside_the_proven_families_or_on_a_contradictory_olmoe_config_is_refused_before_any_hook():
    modellab = load_modellab()
    model = _model({})
    assert _trace_until_prompt(modellab, model, "olmoe", CHOSEN) == "admitted"
    for model_type in ("llama", "qwen3_5_moe", "olmo2"):
        assert "Qwen3.5 and OLMoE" in _trace_until_prompt(modellab, model, model_type, CHOSEN)
    assert "sliding_window" in _trace_until_prompt(modellab, model, "olmoe", CHOSEN, sliding_window=4096)
    mixed = ["full_attention", "sliding_attention"] * 2
    assert "layer types" in _trace_until_prompt(modellab, model, "olmoe", CHOSEN, layer_types=mixed)


def test_a_trace_result_takes_its_head_dim_from_the_attention_module_when_the_config_states_none():
    torch = require_torch()
    modellab = load_modellab()
    registry = {"sdpa": _sdpa}
    model = _model(registry, heads=4, kv_heads=2)
    forward = type(model).forward
    model.forward = lambda ids, **_kwargs: types.SimpleNamespace(logits=forward(model, ids))
    # OLMoE's config states no head_dim.
    text_config = types.SimpleNamespace(num_attention_heads=4, num_key_value_heads=2)
    model.config = types.SimpleNamespace(
        model_type="olmoe", _attn_implementation="sdpa", get_text_config=lambda decoder=False: text_config
    )
    model.generation_config = types.SimpleNamespace(eos_token_id=None)

    def generate(ids, logits_processor, stopping_criteria, max_new_tokens, **_kwargs):
        # Each attention module keeps its own history: after the prompt, only the newest token is fed.
        sequence, feed = ids, ids
        for _ in range(max_new_tokens):
            scores = model(feed).logits[:, -1]
            for processor in logits_processor:
                scores = processor(sequence, scores)
            feed = scores.argmax(-1, keepdim=True)
            sequence = torch.cat([sequence, feed], 1)
            if stopping_criteria[0](sequence, scores).all():
                break
        return sequence

    model.generate = generate
    tokenizer = types.SimpleNamespace(
        eos_token_id=None,
        unk_token_id=0,
        convert_tokens_to_ids=lambda _token: 0,
        decode=lambda token_ids: "".join(f"<{token}>" for token in token_ids),
    )
    modellab._SLOT.loaded, modellab._SLOT.key = (tokenizer, model), "k"
    with tempfile.TemporaryDirectory() as directory:
        job = {
            "modelKey": "k",
            "layers": [0],
            "prompt": "p",
            "maxNewTokens": 2,
            "seed": 1,
            "tracePath": os.path.join(directory, "trace.jsonl"),
            "resultPath": os.path.join(directory, "result.json"),
            "traceByteCap": 100_000,
            "attentionPath": os.path.join(directory, "attention.jsonl"),
            "attentionLayers": CHOSEN,
            "attentionTopKeys": TOP_KEYS,
            "attentionByteCap": 100_000,
        }
        with (
            mock.patch.object(modellab, "_attention_functions", return_value=registry),
            mock.patch.object(modellab, "prompt_ids", return_value=mock.Mock(to=lambda _device: torch.tensor(PROMPT))),
            mock.patch.object(modellab, "_Tracer", return_value=None),
            mock.patch.object(modellab, "measured_facts", lambda _model, blocks: {"blockCount": len(blocks)}),
            mock.patch.object(modellab, "DynamicCache", return_value=None),
            mock.patch.object(modellab, "LogitsProcessorList", list),
            mock.patch.object(modellab, "StoppingCriteriaList", list),
            mock.patch.object(modellab, "TOP_CANDIDATES", TOP_KEYS),
            mock.patch.object(modellab, "emit"),
            mock.patch.object(torch.cuda, "reset_peak_memory_stats"),
            mock.patch.object(torch.cuda, "synchronize"),
        ):
            modellab.trace(job, modellab._Run("j"))
        with open(job["resultPath"], encoding="utf-8") as handle:
            attention = json.load(handle)["attention"]
    assert attention["headDim"] == model.model.layers[CHOSEN[0]].self_attn.head_dim == HEAD_DIM
    assert attention["records"] == 2 * len(CHOSEN)
    assert _hooks(model) == 0
    assert registry["sdpa"] is _sdpa


def test_qwen_attention_needs_a_block_that_names_itself_full_attention():
    modellab = load_modellab()
    model = _model({})
    for index, block in enumerate(model.model.layers):
        block.block_type = "full_attention" if index == 3 else "linear_attention"
    assert _trace_until_prompt(modellab, model, "qwen3_5", [3]) == "admitted"
    assert "blocks [1]" in _trace_until_prompt(modellab, model, "qwen3_5", CHOSEN)
    del model.model.layers[3].block_type
    assert "blocks [3]" in _trace_until_prompt(modellab, model, "qwen3_5_text", [3])


if __name__ == "__main__":
    run_standalone(globals())

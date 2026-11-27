"""Self-checks for modellab.py's expert routing on real torch. Run: ``python3 tools/training/test_modellab_routing.py``.

Assert-based with no framework, like test_modellab_lens.py. Only transformers is stubbed; torch is the real package (the
CPU wheel in the root dev venv). The model is a stand-in shaped like a transformers top-k MoE: each routed block's
``mlp.gate`` returns (logits, bfloat16 weights, expert ids) and its ``mlp.experts`` consumes the last two. Where torch
is not installed, every test skips with the reason.
"""

import contextlib
import types
from unittest import mock

from modellablib import JobError, routing_record
from test_stubs import load_modellab, require_torch, run_standalone

HIDDEN = 8
VOCAB = 13
EXPERTS = 6
TOP_K = 3
# Block 1 is dense: it has no router.
ROUTED = [0, 2, 3]


def _model(normalized=False):
    """Embeddings, three routed blocks and a dense one, and a head. Blocks act per position, so a decode forward may
    pass only the newest token, as with a cache."""
    torch = require_torch()
    nn = torch.nn
    torch.manual_seed(0)

    class Router(nn.Module):
        def __init__(self):
            super().__init__()
            self.weight = nn.Parameter(torch.randn(EXPERTS, HIDDEN))
            self.top_k, self.num_experts, self.norm_topk_prob = TOP_K, EXPERTS, normalized

        def forward(self, x):
            logits = nn.functional.linear(x, self.weight)
            weights, ids = torch.softmax(logits.float(), -1).topk(TOP_K, -1)
            if self.norm_topk_prob:
                weights = weights / weights.sum(-1, keepdim=True)
            return logits, weights.to(torch.bfloat16), ids

    class Experts(nn.Module):
        def __init__(self):
            super().__init__()
            self.weight = nn.Parameter(torch.randn(EXPERTS, HIDDEN, HIDDEN) / HIDDEN)
            self.received = []

        def forward(self, x, ids, weights):
            self.received.append((ids.clone(), weights.clone()))
            chosen = torch.einsum("th,tkhd->tkd", x, self.weight[ids])
            return (chosen * weights.float().unsqueeze(-1)).sum(1)

    class Block(nn.Module):
        def __init__(self, routed):
            super().__init__()
            self.mlp = nn.Module()
            if routed:
                self.mlp.gate, self.mlp.experts = Router(), Experts()
            else:
                self.mlp.up = nn.Linear(HIDDEN, HIDDEN)

        def forward(self, hidden):
            x = hidden[0]
            if hasattr(self.mlp, "gate"):
                logits, weights, ids = self.mlp.gate(x)
                return hidden + self.mlp.experts(x, ids, weights).unsqueeze(0)
            return hidden + self.mlp.up(x).unsqueeze(0)

    class Model(nn.Module):
        def __init__(self):
            super().__init__()
            self.embed = nn.Embedding(VOCAB, HIDDEN)
            self.layers = nn.ModuleList(Block(i in ROUTED) for i in range(4))
            self.head = nn.Linear(HIDDEN, VOCAB)

        def forward(self, ids):
            hidden = self.embed(ids)
            for block in self.layers:
                hidden = block(hidden)
            return self.head(hidden)

    return Model().eval()


def _generate(model, processor, prompt, steps, eos=None):
    """Greedy decoding as generate runs it: the prompt in one forward, then one token a forward, the processor once
    per token after its forward. Returns (generated ids, last logits)."""
    torch = require_torch()
    generated, feed, logits = [], prompt, None
    with torch.inference_mode():
        for _ in range(steps):
            logits = model(feed)
            scores = logits[:, -1]
            if processor is not None:
                assert processor(None, scores) is scores
            token = int(torch.argmax(scores[0]))
            generated.append(token)
            if token == eos:
                break
            feed = torch.tensor([[token]])
    return generated, logits


def _hooks(model):
    return sum(len(m._forward_hooks) + len(m._forward_pre_hooks) for m in model.modules())


def _decoded(bits):
    torch = require_torch()
    return torch.tensor(bits, dtype=torch.int16).view(torch.bfloat16)


def test_routers_are_the_blocks_with_a_top_k_gate():
    modellab = load_modellab()
    model = _model()
    assert [index for index, _ in modellab.routers_of(model.layers)] == ROUTED
    assert modellab.routers_of(model.layers)[0][1] is model.layers[0].mlp.gate
    model.layers[2].mlp.gate.top_k = True  # a bool is no count
    del model.layers[3].mlp.gate.norm_topk_prob
    assert [index for index, _ in modellab.routers_of(model.layers)] == [0]
    assert modellab.routers_of([types.SimpleNamespace(), types.SimpleNamespace(mlp=None)]) == []


def test_each_step_keeps_the_ids_and_exact_weights_the_experts_received_at_the_predicting_position():
    torch = require_torch()
    modellab = load_modellab()
    for normalized in (False, True):
        model = _model(normalized)
        prompt = torch.tensor([[1, 2, 3, 4, 5]])
        with contextlib.ExitStack() as stack:
            routing = modellab._Routing(stack, modellab.routers_of(model.layers))
            generated, _ = _generate(model, routing, prompt, 3)
        assert _hooks(model) == 0
        steps = routing.recorded(len(generated))
        # Prefill, then two decode steps.
        assert len(steps) == 3
        for step, (ids, bits) in enumerate(steps):
            assert len(ids) == len(bits) == len(ROUTED)
            for row, block in enumerate(ROUTED):
                received_ids, received_weights = model.layers[block].mlp.experts.received[step]
                # Row -1: the prompt's last position at step 0, the one new token after.
                assert received_ids.shape[0] == (5 if step == 0 else 1)
                assert ids[row] == received_ids[-1].tolist()
                assert torch.equal(_decoded(bits[row]), received_weights[-1])
                assert all(0 <= value <= 16256 for value in bits[row])
            record = routing_record(step, ids, bits)["routing"]
            assert record == {"s": step, "x": ids, "w": bits}
        if normalized:
            sums = [float(_decoded(row).float().sum()) for _, bits in steps for row in bits]
            assert all(abs(total - 1) < 1e-2 for total in sums), sums


def test_the_hooks_change_no_value():
    torch = require_torch()
    modellab = load_modellab()
    prompt = torch.tensor([[3, 1, 4, 1, 5]])
    off = _generate(_model(), None, prompt, 4)
    model = _model()
    with contextlib.ExitStack() as stack:
        on = _generate(model, modellab._Routing(stack, modellab.routers_of(model.layers)), prompt, 4)
    assert on[0] == off[0]
    assert torch.equal(on[1], off[1])


def test_an_early_eos_records_one_step_per_generated_token():
    torch = require_torch()
    modellab = load_modellab()
    prompt = torch.tensor([[2, 7]])
    tokens, _ = _generate(_model(), None, prompt, 4)
    eos = tokens[1]
    model = _model()
    with contextlib.ExitStack() as stack:
        routing = modellab._Routing(stack, modellab.routers_of(model.layers))
        generated, _ = _generate(model, routing, prompt, 4, eos=eos)
    assert generated == tokens[: tokens.index(eos) + 1]
    assert len(generated) < 4
    assert len(routing.recorded(len(generated))) == len(generated)
    try:
        routing.recorded(4)
    except RuntimeError as error:
        assert f"{len(generated)} steps of 4" in str(error), str(error)
    else:
        raise AssertionError("Fewer routing steps than generated tokens must be refused at saving.")


def test_a_second_router_call_before_the_processor_overwrites_the_first():
    torch = require_torch()
    modellab = load_modellab()
    model = _model()
    with contextlib.ExitStack() as stack, torch.inference_mode():
        routing = modellab._Routing(stack, modellab.routers_of(model.layers))
        model(torch.tensor([[1, 2]]))
        model(torch.tensor([[9, 8, 7]]))
        routing(None, torch.zeros(1, VOCAB))
    ((ids, bits),) = routing.recorded(1)
    for row, block in enumerate(ROUTED):
        received_ids, received_weights = model.layers[block].mlp.experts.received[-1]
        assert ids[row] == received_ids[-1].tolist()
        assert torch.equal(_decoded(bits[row]), received_weights[-1])


def test_a_step_without_every_router_is_refused():
    torch = require_torch()
    modellab = load_modellab()
    model = _model()
    with contextlib.ExitStack() as stack, torch.inference_mode():
        routing = modellab._Routing(stack, modellab.routers_of(model.layers))
        model(torch.tensor([[1]]))
        del routing._stash[2]
        try:
            routing(None, torch.zeros(1, VOCAB))
        except RuntimeError as error:
            assert "blocks [2]" in str(error), str(error)
        else:
            raise AssertionError("A routing step missing block 2's router was recorded.")
    assert routing.steps == []


def test_a_later_step_whose_router_did_not_fire_is_refused_not_given_the_previous_steps_row():
    torch = require_torch()
    modellab = load_modellab()
    model = _model()
    with contextlib.ExitStack() as stack, torch.inference_mode():
        routing = modellab._Routing(stack, modellab.routers_of(model.layers))
        model(torch.tensor([[1, 2]]))
        routing(None, torch.zeros(1, VOCAB))
        # Step 1: every router but block 2's.
        hidden = torch.zeros(1, HIDDEN)
        model.layers[0].mlp.gate(hidden)
        model.layers[3].mlp.gate(hidden)
        try:
            routing(None, torch.zeros(1, VOCAB))
        except RuntimeError as error:
            assert "blocks [2]" in str(error), str(error)
        else:
            raise AssertionError("Step 1 was recorded with block 2's row left over from step 0.")
    assert len(routing.steps) == 1


def test_saving_moves_every_row_to_the_host_in_one_transfer():
    torch = require_torch()
    modellab = load_modellab()
    model = _model()
    with contextlib.ExitStack() as stack:
        routing = modellab._Routing(stack, modellab.routers_of(model.layers))
        generated, _ = _generate(model, routing, torch.tensor([[4, 4, 4]]), 4)
    original = torch.Tensor.tolist
    calls = []

    def counted(tensor):
        calls.append(tuple(tensor.shape))
        return original(tensor)

    with mock.patch.object(torch.Tensor, "tolist", counted):
        steps = routing.recorded(len(generated))
    assert calls == [(len(generated), 2, len(ROUTED), TOP_K)]
    assert all(isinstance(value, int) for ids, bits in steps for row in ids + bits for value in row)


def test_a_constructor_failing_after_its_first_hook_leaves_no_hook():
    torch = require_torch()
    modellab = load_modellab()
    model = _model()
    original = torch.nn.Module.register_forward_hook
    count = [0]

    def register(module, *args, **kwargs):
        count[0] += 1
        if count[0] == 2:
            raise RuntimeError("injected after the first hook")
        return original(module, *args, **kwargs)

    try:
        with (
            contextlib.ExitStack() as stack,
            mock.patch.object(torch.nn.Module, "register_forward_hook", register),
        ):
            modellab._Routing(stack, modellab.routers_of(model.layers))
    except RuntimeError as error:
        assert "first hook" in str(error), str(error)
    else:
        raise AssertionError("The injected registration failure must surface.")
    assert _hooks(model) == 0


def test_a_router_output_of_another_shape_or_type_is_refused():
    torch = require_torch()
    modellab = load_modellab()
    model = _model()
    stack = contextlib.ExitStack()
    routing = modellab._Routing(stack, modellab.routers_of(model.layers))
    stack.close()
    hook = routing._hook(0, TOP_K, EXPERTS)
    logits = torch.zeros(2, EXPERTS)
    weights = torch.zeros(2, TOP_K, dtype=torch.bfloat16)
    ids = torch.zeros(2, TOP_K, dtype=torch.long)
    bad = [
        logits,
        (logits, weights),
        (logits, weights.float(), ids),
        (logits, weights, ids.float()),
        (torch.zeros(2, EXPERTS + 1), weights, ids),
        (torch.zeros(EXPERTS), weights, ids),
        (logits, weights[:, :2], ids[:, :2]),
        (logits, weights[:1], ids),
        (logits, weights, ids[:1]),
    ]
    for output in bad:
        try:
            hook(None, None, output)
        except JobError:
            continue
        raise AssertionError(f"A router output {output!r} must be refused.")
    assert routing._stash == {}
    hook(None, None, (logits, weights, ids.int()))
    assert set(routing._stash) == {0}


def test_a_routed_model_set_to_output_its_router_logits_is_refused_before_any_hook():
    modellab = load_modellab()
    model = _model()
    reached = RuntimeError("admitted")

    def outcome(output_router_logits):
        text_config = types.SimpleNamespace(output_router_logits=output_router_logits)
        config = types.SimpleNamespace(
            model_type="olmoe", _attn_implementation="sdpa", get_text_config=lambda decoder=False: text_config
        )
        modellab._SLOT.loaded = (None, types.SimpleNamespace(model=model, config=config))
        modellab._SLOT.key = "k"
        with mock.patch.object(modellab, "prompt_ids", side_effect=reached):
            try:
                modellab.trace({"modelKey": "k", "layers": [0], "prompt": "p"}, modellab._Run("j"))
            except JobError as error:
                return str(error)
            except RuntimeError as error:
                assert error is reached
                return "admitted"
        raise AssertionError("trace() must stop at the refusal or at the prompt.")

    assert "output_router_logits" in outcome(True)
    assert outcome(False) == "admitted"
    assert _hooks(model) == 0


if __name__ == "__main__":
    run_standalone(globals())

"""Self-checks for modellab.py's logit lens on real torch. Run: ``python3 tools/training/test_modellab_lens.py``.

Assert-based with no framework, like test_modellablib.py. Only transformers is stubbed; torch is the real package (the
CPU wheel in the root dev venv). Where torch is not installed, every test skips with the reason.
"""

import contextlib
import math
import sys
import types
import unittest
from unittest import mock

from modellablib import lens_record

try:
    import torch
except ImportError:
    torch = None

HIDDEN = 8
VOCAB = 11
BLOCKS = 3
TOP = 4


def _require_torch():
    if torch is None:
        raise unittest.SkipTest("torch is not installed: the root dev venv carries CPU torch on linux x86_64 only.")
    return torch


def _load_modellab():
    """The real script on real torch, with transformers reduced to the names it imports. Leaves no stub behind."""
    _require_torch()
    transformers = types.SimpleNamespace(
        __version__="stub",
        AutoModelForCausalLM=None,
        AutoTokenizer=None,
        DynamicCache=None,
        LogitsProcessor=object,
        LogitsProcessorList=None,
        StoppingCriteria=object,
        StoppingCriteriaList=None,
    )
    with mock.patch.dict(sys.modules, {"transformers": transformers}):
        sys.modules.pop("modellab", None)
        import modellab

        sys.modules.pop("modellab", None)
    return modellab


def _model():
    """Embeddings, BLOCKS linear blocks, a LayerNorm and an untied head: the parts _Lens reads."""
    torch = _require_torch()
    nn = torch.nn
    torch.manual_seed(0)

    class Model(nn.Module):
        def __init__(self):
            super().__init__()
            self.embed = nn.Embedding(VOCAB, HIDDEN)
            self.layers = nn.ModuleList(nn.Linear(HIDDEN, HIDDEN) for _ in range(BLOCKS))
            self.norm = nn.LayerNorm(HIDDEN)
            self.head = nn.Linear(HIDDEN, VOCAB)
            text_config = types.SimpleNamespace(hidden_size=HIDDEN)
            self.config = types.SimpleNamespace(get_text_config=lambda decoder=False: text_config)

        def get_input_embeddings(self):
            return self.embed

        def forward(self, ids):
            hidden = self.embed(ids)
            for block in self.layers:
                hidden = block(hidden)
            return types.SimpleNamespace(logits=self.head(self.norm(hidden)))

    return Model().eval()


def _lens(modellab, model, stack):
    return modellab._Lens(stack, model, model.layers, TOP, model.norm, model.head)


def test_each_block_reads_through_the_head_and_the_last_row_is_the_models_own():
    torch = _require_torch()
    modellab = _load_modellab()
    model = _model()
    stack = contextlib.ExitStack()
    lens = _lens(modellab, model, stack)
    ids = torch.tensor([[1, 2, 3, 4]])
    with torch.inference_mode():
        logits = model(ids).logits
        scores = logits[:, -1]
        assert lens(ids, scores) is scores
        stack.close()
        # Removed hooks record nothing more: this pass, by hand, is the expected readout per block.
        readouts, hidden = [], model.embed(ids)
        for block in model.layers:
            hidden = block(hidden)
            readouts.append(torch.log_softmax(model.head(model.norm(hidden[0, -1])), -1))
        model(ids)
    assert lens._stash == {}
    assert lens._raw is None

    chosen, top_ids, top_logprobs, at, ranks, entropies = lens.steps[0]
    assert top_ids.shape == top_logprobs.shape == (BLOCKS, TOP)
    assert at.shape == ranks.shape == entropies.shape == (BLOCKS,)
    assert top_logprobs.dtype == torch.float32
    assert lens.vocab_size == VOCAB
    assert lens.tied_embeddings is False
    assert chosen.item() == torch.argmax(scores[0]).item()

    # The last row is the model's raw logits, not a readout of the last block.
    raw = torch.log_softmax(logits[0, -1], -1)
    assert torch.equal(top_logprobs[-1], raw.topk(TOP).values)
    assert torch.equal(top_ids[-1], raw.topk(TOP).indices)
    assert at[-1].item() == raw[chosen].item()
    assert ranks[-1].item() == 1  # greedy: the chosen token is the raw argmax
    assert math.isclose(entropies[-1].item(), -(raw.exp() * raw).sum().item(), rel_tol=1e-6)

    for block in range(BLOCKS - 1):
        assert torch.allclose(top_logprobs[block], readouts[block].topk(TOP).values, atol=1e-6)
        assert torch.equal(top_ids[block], readouts[block].topk(TOP).indices)
        assert torch.allclose(at[block], readouts[block][chosen], atol=1e-6)


def test_a_nan_readout_stays_nan_on_record_and_is_written_null():
    torch = _require_torch()
    modellab = _load_modellab()
    with contextlib.ExitStack() as stack:
        lens = _lens(modellab, _model(), stack)
        torch.manual_seed(1)
        lens._stash = {0: torch.full((HIDDEN,), math.nan), 1: torch.randn(HIDDEN)}
        lens._raw = torch.randn(VOCAB)
        with torch.inference_mode():
            lens(None, lens._raw.view(1, -1))

    _, ids, logprobs, chosen, ranks, entropies = lens.recorded()[0]
    assert math.isnan(entropies[0])
    assert all(math.isfinite(value) for value in entropies[1:])

    texts = {token: f"t{token}" for token in range(VOCAB)}
    record = lens_record(0, ids, logprobs, chosen, ranks, entropies, texts)["lens"]
    assert record["e"][0] is None
    assert record["c"][0] is None
    assert record["r"][0] is None
    assert record["p"][0] == [None] * TOP
    assert all(value is not None for value in record["e"][1:] + record["c"][1:] + record["r"][1:])


def test_a_step_without_every_block_state_is_refused():
    torch = _require_torch()
    modellab = _load_modellab()
    with contextlib.ExitStack() as stack:
        lens = _lens(modellab, _model(), stack)
        lens._stash = {0: torch.zeros(HIDDEN)}
        lens._raw = torch.zeros(VOCAB)
        try:
            lens(None, torch.zeros(1, VOCAB))
        except RuntimeError as error:
            assert "blocks [1]" in str(error)
        else:
            raise AssertionError("A lens step missing block 1's state was recorded.")
    assert lens.steps == []


if __name__ == "__main__":
    for name, test in sorted(globals().items()):
        if name.startswith("test_") and callable(test):
            try:
                test()
            except unittest.SkipTest as reason:
                print(f"skip {name}: {reason}")
            else:
                print(f"ok {name}")

"""Shared helpers of the Model Lab self-checks: the torch requirement, modellab.py on stubbed transformers and the
standalone runner. It holds no tests; its test_ name keeps it out of the shipped training runtime."""

import sys
import types
import unittest
from unittest import mock

try:
    import torch
except ImportError:
    torch = None


def require_torch():
    if torch is None:
        raise unittest.SkipTest("torch is not installed: the root dev venv carries CPU torch on linux x86_64 only.")
    return torch


def load_modellab():
    """The real script on real torch, with transformers reduced to the names it imports. Leaves no stub behind."""
    require_torch()
    transformers = types.SimpleNamespace(
        __version__="stub",
        AutoModelForCausalLM=None,
        AutoTokenizer=None,
        DynamicCache=None,
        LogitsProcessor=object,
        LogitsProcessorList=list,
        StoppingCriteria=object,
        StoppingCriteriaList=list,
    )
    with mock.patch.dict(sys.modules, {"transformers": transformers}):
        sys.modules.pop("modellab", None)
        import modellab

        sys.modules.pop("modellab", None)
    return modellab


def run_standalone(names):
    """Runs every test_ callable of a module's globals in name order, printing ok or the skip reason of each."""
    for name, test in sorted(names.items()):
        if name.startswith("test_") and callable(test):
            try:
                test()
            except unittest.SkipTest as reason:
                print(f"skip {name}: {reason}")
            else:
                print(f"ok {name}")

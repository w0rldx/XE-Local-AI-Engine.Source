# Training runtime

The pinned Python runtime for fine-tuning and GGUF conversion. The pins and the reasons behind them live in the header of
`pyproject.toml`; the runtime's behaviour is in `docs/wiki/18-training.md`. Never `uv sync` in this folder.

## Re-check schedule

The pins (torch 2.11.0 on cu128, torchvision 0.26.0, unsloth 2026.9.6, transformers 5.5.0, bitsandbytes 0.50.2,
Python 3.13) move together. unsloth and unsloth_zoo cap `transformers<=5.5.0`, `torch<2.13.0` and `datasets<4.4.0`, and
the cu128 index ships no torch newer than 2.11, so the held-back dependency alerts listed in `pyproject.toml` cannot be
taken until unsloth moves. Re-check on every unsloth release: read its and unsloth_zoo's `requires_dist` on PyPI, then
follow the bump recipe at the end of the `pyproject.toml` header.

`train.py` still passes `warmup_ratio` to `SFTConfig`. The transformers v5 migration guide says `warmup_ratio` was
replaced by a float `warmup_steps`. Moving to a transformers release that no longer accepts `warmup_ratio` breaks the
run with an unexpected-keyword error, so change that argument in the same bump. Whether 5.5.0 itself already rejects it
was not verified when this note was written, because the runtime is not installed on the documentation host.

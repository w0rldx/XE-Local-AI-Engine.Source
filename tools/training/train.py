"""QLoRA supervised fine-tuning for one training run.

Input is a single ``job.json`` (see ``TrainingJobConfigV1`` on the C# side); output is a LoRA adapter plus its
tokenizer in ``outputDir``, and a JSON-lines protocol on stdout that the host parses.

The protocol (contract version 1). One JSON object per line; the host ignores every line that is not one, because
importing unsloth and torch prints banner text this script does not control:

    {"event": "handshake", "contractVersion": 1, ...versions}
    {"event": "phase",     "phase": "loading" | "tokenizing" | "training" | "saving"}
    {"event": "progress",  "step", "totalSteps", "epoch", "loss", "lr", "vramBytes"}
    {"event": "heartbeat", "phase"}                       -- at least every 30 s during long silent phases
    {"event": "artifact",  "kind": "HfAdapterDir", "path"}
    {"event": "done",      "cancelled": bool}
    {"event": "error",     "category", "message"}

Exit status: 0 on success, 3 on a cooperative SIGTERM stop (so the host records Cancelled rather than Failed),
1 on any error.
"""

# Import order matters: unsloth patches transformers/TRL at import time and warns loudly if it is imported after
# them. This is an unsloth requirement, not a style preference.
import unsloth  # noqa: F401  isort:skip

import argparse
import json
import os
import signal
import sys
import threading
import traceback

from trainlib import (
    CANCELLED_EXIT_CODE,
    CONTRACT_VERSION,
    assert_safe_chat_template_names,
    collect_tools,
    delimiter_before,
    has_tool_calls,
    load_samples,
    to_messages,
)

HEARTBEAT_SECONDS = 20

# LoRA targets for every Llama-architecture model this feature supports (Llama, Qwen, SmolLM, Mistral): the four
# attention projections plus the three MLP projections. The names are shared across the family.
TARGET_MODULES = [
    "q_proj",
    "k_proj",
    "v_proj",
    "o_proj",
    "gate_proj",
    "up_proj",
    "down_proj",
]

_TOOL_PROBE_TOOL = "get_weather"
_TOOL_PROBE_ARGUMENT = "Paris, France"


def emit(event, **fields):
    """Writes one protocol line. Flushed every time: the host's watchdog reads liveness from these arriving."""
    payload = {"event": event}
    payload.update(fields)
    print(json.dumps(payload), flush=True)


def fail(category, message):
    emit("error", category=category, message=message)
    sys.exit(1)


class _Heartbeat:
    """Emits a heartbeat while a long phase produces no other output.

    Loading a 4-bit checkpoint and tokenizing a dataset can both run for minutes in silence, and the host cannot
    tell that apart from a wedged CUDA call. The beat is what makes the inactivity watchdog safe to keep short.
    """

    def __init__(self):
        self._phase = "loading"
        self._stop = threading.Event()
        self._thread = threading.Thread(target=self._run, daemon=True)

    def start(self):
        self._thread.start()

    def phase(self, name):
        self._phase = name
        emit("phase", phase=name)

    def stop(self):
        self._stop.set()

    def _run(self):
        while not self._stop.wait(HEARTBEAT_SECONDS):
            emit("heartbeat", phase=self._phase)


def read_config(path):
    with open(path, encoding="utf-8") as handle:
        config = json.load(handle)
    if config.get("contractVersion") != CONTRACT_VERSION:
        fail("contract", "The job configuration contract version is not supported by this trainer.")
    return config


def render(tokenizer, messages, tools=None):
    return tokenizer.apply_chat_template(
        messages,
        tools=tools or None,
        tokenize=False,
        # Training time: the trailing "assistant turn start" tokens are an inference-time affordance and would
        # pollute the sequence here.
        add_generation_prompt=False,
    )


def verify_tool_template(tokenizer):
    """Renders one synthetic tool call and fails fast when the template drops it.

    Not every base chat template has a tool branch. A template without one either silently renders nothing for
    ``tool_calls`` or raises — and the silent case is the dangerous one: the run would train for hours on a model
    that never sees its own tool-call syntax.
    """
    tools = [
        {
            "type": "function",
            "function": {
                "name": _TOOL_PROBE_TOOL,
                "description": "Get current weather for a location.",
                "parameters": {
                    "type": "object",
                    "properties": {"location": {"type": "string"}},
                    "required": ["location"],
                },
            },
        }
    ]
    messages = [
        {"role": "user", "content": "weather?"},
        {
            "role": "assistant",
            "tool_calls": [
                {
                    "type": "function",
                    "function": {"name": _TOOL_PROBE_TOOL, "arguments": {"location": _TOOL_PROBE_ARGUMENT}},
                }
            ],
        },
        {"role": "tool", "content": "22"},
    ]
    try:
        text = render(tokenizer, messages, tools)
    except Exception as error:  # noqa: BLE001 - any template failure is the same actionable outcome
        fail("template", f"The base model's chat template cannot render tool calls: {error}")

    if _TOOL_PROBE_TOOL not in text or _TOOL_PROBE_ARGUMENT not in text:
        fail(
            "template",
            "The base model's chat template silently drops tool calls, so this dataset cannot train it. "
            "Choose a base checkpoint whose template supports tool use.",
        )


def derive_delimiters(tokenizer):
    """Reads the instruction/response turn delimiters out of the model's OWN chat template."""
    user_marker = "USERCONTENT"
    assistant_marker = "ASSISTANTCONTENT"
    rendered = render(
        tokenizer,
        [
            {"role": "user", "content": user_marker},
            {"role": "assistant", "content": assistant_marker},
        ],
    )

    instruction = delimiter_before(rendered, user_marker, ("user",))
    # Gemma names the assistant turn "model"; every other family in scope names it "assistant".
    response = delimiter_before(rendered, assistant_marker, ("assistant", "model"))
    if not instruction or not response:
        fail(
            "template",
            "The base model's chat template has no recognisable turn delimiters, so loss masking cannot be applied.",
        )
    return instruction, response


def build_dataset(tokenizer, records, tools):
    from datasets import Dataset

    texts = [render(tokenizer, to_messages(record), tools) for record in records]
    return Dataset.from_dict({"text": texts})


def main():
    parser = argparse.ArgumentParser(description="QLoRA supervised fine-tuning for one training run.")
    parser.add_argument("--config", required=True, help="Path to the run's job.json.")
    arguments = parser.parse_args()

    config = read_config(arguments.config)
    options = config.get("options") or {}
    output_dir = config["outputDir"]

    import torch
    from transformers import TrainerCallback
    from trl import SFTConfig, SFTTrainer
    from unsloth import FastLanguageModel, is_bfloat16_supported
    from unsloth.chat_templates import train_on_responses_only

    emit(
        "handshake",
        contractVersion=CONTRACT_VERSION,
        torch=torch.__version__,
        cuda=torch.version.cuda,
        device=torch.cuda.get_device_name(0) if torch.cuda.is_available() else None,
    )

    cancelled = threading.Event()

    def handle_sigterm(_signum, _frame):
        # Cooperative: the trainer finishes the current step and saves. Raising here would produce a traceback exit
        # and lose the adapter, which is why the host sends SIGTERM rather than SIGKILL for an operator cancel.
        cancelled.set()

    signal.signal(signal.SIGTERM, handle_sigterm)

    heartbeat = _Heartbeat()
    heartbeat.start()
    try:
        heartbeat.phase("loading")
        max_seq_length = int(options.get("maxSeqLength", 2048))
        model, tokenizer = FastLanguageModel.from_pretrained(
            model_name=config["basePath"],
            max_seq_length=max_seq_length,
            # None auto-selects bf16 on Ampere and later; forcing it would break an older card for no gain.
            dtype=None,
            load_in_4bit=True,
            local_files_only=True,
        )

        # Before anything is written: the base checkpoint is a user-supplied Hugging Face repo, and its chat
        # template names reach save_pretrained as filenames. Checked at load time so the run fails here rather
        # than after an hour of training.
        assert_safe_chat_template_names(tokenizer, config["basePath"])

        instruction_part, response_part = derive_delimiters(tokenizer)

        model = FastLanguageModel.get_peft_model(
            model,
            r=int(options.get("loraR", 16)),
            target_modules=TARGET_MODULES,
            lora_alpha=int(options.get("loraAlpha", 16)),
            lora_dropout=float(options.get("loraDropout", 0)),
            bias="none",
            # "unsloth", not True: this selects unsloth's offloaded checkpointing kernel, which is the whole reason
            # this stack fits a long sequence at all.
            use_gradient_checkpointing="unsloth",
            random_state=int(options.get("seed", 3407)),
            use_rslora=False,
            loftq_config=None,
        )

        heartbeat.phase("tokenizing")
        records = load_samples(config["datasetPath"], config.get("holdoutSequences"))
        if not records:
            fail("dataset", "Every sample in the frozen dataset was excluded; there is nothing to train on.")
        tools = collect_tools(records)
        # Only meaningful once the dataset is known to contain calls: a template with no tool branch is only a
        # problem for a dataset that needs one.
        if has_tool_calls(records):
            verify_tool_template(tokenizer)
        dataset = build_dataset(tokenizer, records, tools)

        class ProgressCallback(TrainerCallback):
            def on_log(self, args, state, control, logs=None, **kwargs):
                if not logs:
                    return
                payload = {
                    "step": int(state.global_step),
                    "totalSteps": int(state.max_steps or 0),
                    "epoch": float(state.epoch) if state.epoch is not None else None,
                    "loss": logs.get("loss"),
                    "lr": logs.get("learning_rate"),
                }
                if torch.cuda.is_available():
                    payload["vramBytes"] = int(torch.cuda.memory_reserved())
                emit("progress", **payload)

            def on_step_end(self, args, state, control, **kwargs):
                if cancelled.is_set():
                    # A latch: transformers never unsets it, and the loop breaks after this step completes.
                    control.should_training_stop = True
                return control

        trainer = SFTTrainer(
            model=model,
            # TRL 0.24 renamed this from `tokenizer`; passing the old name is silently ignored.
            processing_class=tokenizer,
            train_dataset=dataset,
            args=SFTConfig(
                output_dir=os.path.join(config["workDir"], "trainer"),
                per_device_train_batch_size=int(options.get("perDeviceTrainBatchSize", 2)),
                gradient_accumulation_steps=int(options.get("gradientAccumulationSteps", 4)),
                learning_rate=float(options.get("learningRate", 2e-4)),
                warmup_ratio=float(options.get("warmupRatio", 0.03)),
                num_train_epochs=int(options.get("epochs", 1)),
                optim=options.get("optimizer", "adamw_8bit"),
                seed=int(options.get("seed", 3407)),
                logging_steps=1,
                bf16=is_bfloat16_supported(),
                fp16=not is_bfloat16_supported(),
                max_length=max_seq_length,
                # Packing groups several conversations into one block, which scrambles the turn boundaries the loss
                # masking below depends on.
                packing=False,
                dataset_text_field="text",
                report_to="none",
                # Live-found: `datasets` defaults num_proc from the CPU count and forks a multiprocessing Manager for
                # tokenization; inside this sandboxed, CUDA-initialized process the forked child dies (EOFError on the
                # manager pipe). One process is correct here - the datasets are small and the model is
                # already on the GPU.
                dataset_num_proc=1,
            ),
            callbacks=[ProgressCallback()],
        )

        # Masks everything except the assistant response spans to -100. Applied AFTER construction so it operates
        # on the already-tokenized dataset regardless of how TRL templated it.
        trainer = train_on_responses_only(
            trainer,
            instruction_part=instruction_part,
            response_part=response_part,
        )

        heartbeat.phase("training")
        trainer.train()

        heartbeat.phase("saving")
        model.save_pretrained(output_dir)
        tokenizer.save_pretrained(output_dir)
        emit("artifact", kind="HfAdapterDir", path=output_dir)
        emit("done", cancelled=cancelled.is_set())
    except SystemExit:
        raise
    except Exception as error:  # noqa: BLE001 - every failure has to reach the host as one protocol line
        # str(EOFError()) is "" - an empty message would leave the host with a failed run and no visible reason,
        # so the exception type is the floor.
        message = str(error).strip() or type(error).__name__
        emit("error", category=type(error).__name__, message=message[:1000])
        traceback.print_exc(file=sys.stderr)
        sys.exit(1)
    finally:
        heartbeat.stop()

    sys.exit(CANCELLED_EXIT_CODE if cancelled.is_set() else 0)


if __name__ == "__main__":
    main()

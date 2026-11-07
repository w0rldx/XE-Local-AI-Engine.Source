"""Model Lab: reads chosen layers of a local safetensors checkpoint with forward hooks that change no value.

Three kinds of ``job.json``. A trace (``ModelLabTraceJobV1`` on the C# side) records statistics while generating; the
host fixes the layers, token count and byte cap. A steering job (``ModelLabSteeringJobV1``) derives a control vector
from two contrasting prompt sets and writes it with a random one of the same norm. A source job
(``ModelLabSourceJobV1``) indexes the PyTorch code of a model from its config.json alone, on the meta device: no
weights, no GPU. Nothing in a job can name code to run. Offline, no remote code, no unsloth.

The protocol is the trainer's JSON-lines shape with its own contract version, on stdout:

    {"event": "handshake", "contractVersion": 3, ...versions}
    {"event": "phase",     "phase": "loading" | "tracing" | "deriving" | "exporting" | "saving"}
    {"event": "progress",  "step", "totalSteps", "vramBytes"}    a token, or a contrast prompt
    {"event": "heartbeat", "phase"}          until the first progress, and while saving; progress proves life between
    {"event": "done",      "cancelled": bool}
    {"event": "error",     "category", "message"}

Exit status: 0 on success, 3 on a cooperative SIGTERM stop, 1 on any error.
"""

import argparse
import functools
import hashlib
import inspect
import json
import linecache
import os
import platform
import signal
import sys
import threading
import time

import torch
import transformers
from transformers import AutoModelForCausalLM, AutoTokenizer, StoppingCriteria, StoppingCriteriaList

from modellablib import (
    CONTRACT_VERSION,
    SOURCE_KIND,
    STEERING_KIND,
    CappedTraceWriter,
    JobError,
    SourceRefusalError,
    block_index_of,
    control_vector_bytes,
    describe_failure,
    discover_block_path,
    heartbeat_counts,
    layer_kind,
    layer_types_follow_cut,
    named_files,
    op_graphs,
    op_location,
    out_of_range_layers,
    read_raw_config,
    register_anchors,
    relative_source_file,
    representatives,
    sanitized_export_error,
    scaled_to_norm,
    serialized_result,
    source_file_path,
    steering_vector,
    trace_record,
    validate_job,
    vector_norm,
)
from trainlib import CANCELLED_EXIT_CODE

HEARTBEAT_SECONDS = 20
TOP_DIMENSIONS = 8
PERCENTILES = (0.01, 0.5, 0.99)


def emit(event, **fields):
    # os.write, not print: the SIGTERM handler and the heartbeat thread both write here, and one write is one line.
    os.write(1, (json.dumps({"event": event, **fields}) + "\n").encode())


class _Run:
    """What the signal handler, the heartbeat and the stopping criterion share."""

    def __init__(self):
        self.phase = "loading"
        self.progress_seen = False
        self.stop = threading.Event()
        self._finished = threading.Event()

    def enter(self, phase):
        self.phase = phase
        emit("phase", phase=phase)

    def on_sigterm(self, _signum, _frame):
        self.stop.set()
        # Generation stops itself at the next token. Any other phase has no such point, and nothing worth keeping.
        if self.phase != "tracing":
            emit("done", cancelled=True)
            os._exit(CANCELLED_EXIT_CODE)

    def start_heartbeat(self):
        threading.Thread(target=self._beat, daemon=True).start()

    def finish(self):
        self._finished.set()

    def _beat(self):
        while not self._finished.wait(HEARTBEAT_SECONDS):
            if heartbeat_counts(self.phase, self.progress_seen):
                emit("heartbeat", phase=self.phase)


class _Tracer:
    """Per-token statistics from forward hooks on the chosen layers. They return nothing, so they change no value."""

    def __init__(self, blocks, chosen, writer):
        self._writer = writer
        self._first = chosen[0]
        self._step = -1
        self._quantiles = torch.tensor(PERCENTILES, device="cuda")
        self._handles = [
            blocks[i].register_forward_hook(self._hook(i, layer_kind(getattr(blocks[i], "block_type", None))))
            for i in chosen
        ]

    def _hook(self, index, kind):
        def run(_module, _args, output):
            if index == self._first:
                self._step += 1
            if self._writer.truncated:
                self._writer.dropped += 1
                return
            hidden = output[0] if isinstance(output, tuple) else output
            # Only the newest position: a prompt pass would otherwise cost one record per prompt token.
            values = hidden[0, -1].float()
            top = values.abs().topk(TOP_DIMENSIONS)
            self._writer.write(
                trace_record(
                    self._step,
                    index,
                    kind,
                    values.norm().item(),
                    torch.quantile(values, self._quantiles).tolist(),
                    top.indices.tolist(),
                    values[top.indices].tolist(),
                )
            )

        return run

    def remove(self):
        for handle in self._handles:
            handle.remove()


class _ProgressAndStop(StoppingCriteria):
    def __init__(self, run, prompt_tokens, total):
        self._run = run
        self._prompt_tokens = prompt_tokens
        self._total = total

    def __call__(self, input_ids, scores, **kwargs):
        self._run.progress_seen = True
        emit(
            "progress",
            step=input_ids.shape[1] - self._prompt_tokens,
            totalSteps=self._total,
            vramBytes=torch.cuda.memory_allocated(),
        )
        return torch.full((input_ids.shape[0],), self._run.stop.is_set(), device=input_ids.device, dtype=torch.bool)


def blocks_of(model):
    inner = model.model
    return inner.language_model.layers if hasattr(inner, "language_model") else inner.layers


def prompt_ids(tokenizer, prompt):
    if not tokenizer.chat_template:
        return tokenizer(prompt, return_tensors="pt").input_ids
    rendered = tokenizer.apply_chat_template(
        [{"role": "user", "content": prompt}], tokenize=False, add_generation_prompt=True, enable_thinking=False
    )
    return tokenizer(rendered, return_tensors="pt", add_special_tokens=False).input_ids


def stop_tokens(tokenizer, generation_eos):
    # The explicit list replaces the checkpoint's own, so its stops are carried over. A checkpoint with no
    # generation_config stops only on its EOS, and a chat model would run past its end of turn.
    tokens = {tokenizer.eos_token_id}
    tokens.update(generation_eos if isinstance(generation_eos, (list, tuple)) else [generation_eos])
    end_of_turn = tokenizer.convert_tokens_to_ids("<|im_end|>")
    if isinstance(end_of_turn, int) and end_of_turn != tokenizer.unk_token_id:
        tokens.add(end_of_turn)
    return sorted(token for token in tokens if token is not None)


def load_model(job, run):
    run.enter("loading")
    if not torch.cuda.is_available():
        raise RuntimeError("CUDA is not available to the training runtime, and Model Lab runs on the GPU only.")
    path = job["checkpointPath"]
    # A local directory fixed by the host, offline, so there is no revision to pin.
    tokenizer = AutoTokenizer.from_pretrained(path, local_files_only=True, trust_remote_code=False)  # nosec B615
    model = AutoModelForCausalLM.from_pretrained(  # nosec B615
        path, dtype=torch.bfloat16, local_files_only=True, trust_remote_code=False, use_safetensors=True
    )
    return tokenizer, model.to("cuda").eval()


def measured_facts(model, blocks):
    """What every result carries about where it was measured."""
    return {
        "peakVramBytes": torch.cuda.max_memory_allocated(),
        "blockCount": len(blocks),
        "modelClass": type(model).__name__,
        "device": torch.cuda.get_device_name(0),
        "torch": torch.__version__,
        "transformers": transformers.__version__,
        "python": platform.python_version(),
    }


def write_result(job, result):
    with open(job["resultPath"], "w", encoding="utf-8") as handle:
        json.dump(result, handle, ensure_ascii=False)


def trace(job, run):
    tokenizer, model = load_model(job, run)
    blocks = blocks_of(model)
    if outside := out_of_range_layers(job["layers"], len(blocks)):
        raise JobError(f"The job names layers {outside}, and this checkpoint has {len(blocks)} blocks.")

    ids = prompt_ids(tokenizer, job["prompt"]).to("cuda")
    prompt_tokens = ids.shape[1]
    total = job["maxNewTokens"]
    torch.manual_seed(job["seed"])
    torch.cuda.manual_seed_all(job["seed"])
    torch.cuda.reset_peak_memory_stats()

    writer = CappedTraceWriter(job["tracePath"], job["traceByteCap"])
    tracer = _Tracer(blocks, job["layers"], writer)
    run.enter("tracing")
    started = time.perf_counter()
    try:
        with torch.inference_mode():
            output = model.generate(
                ids,
                attention_mask=torch.ones_like(ids),
                do_sample=False,
                max_new_tokens=total,
                use_cache=True,
                eos_token_id=stop_tokens(tokenizer, model.generation_config.eos_token_id),
                pad_token_id=tokenizer.eos_token_id,
                stopping_criteria=StoppingCriteriaList([_ProgressAndStop(run, prompt_tokens, total)]),
            )
        torch.cuda.synchronize()
        seconds = time.perf_counter() - started
    finally:
        tracer.remove()
    if run.stop.is_set():
        return True

    run.enter("saving")
    writer.close()
    generated = output[0, prompt_tokens:].tolist()
    write_result(
        job,
        {
            "promptTokens": prompt_tokens,
            "tokens": [{"id": token, "text": tokenizer.decode([token])} for token in generated],
            "text": tokenizer.decode(generated),
            "seconds": round(seconds, 3),
            **measured_facts(model, blocks),
        },
    )
    return False


class _ResidualReader:
    """The chosen block's output for one prompt: its mean over positions and its residuals' mean length. Reads only."""

    def __init__(self, block):
        self._last = torch.empty(0)
        self._handle = block.register_forward_hook(self._read)

    def _read(self, _module, _args, output):
        self._last = (output[0] if isinstance(output, tuple) else output)[0].float()

    def take(self):
        residuals = self._last
        return residuals.mean(0), residuals.norm(dim=-1).mean().item()

    def remove(self):
        self._handle.remove()


def derive(job, run):
    """Writes the vector and its random control, the only outputs that leave this process besides the result."""
    load_started = time.perf_counter()
    tokenizer, model = load_model(job, run)
    load_seconds = time.perf_counter() - load_started
    blocks = blocks_of(model)
    layer = job["layer"]
    if layer >= len(blocks):
        raise JobError(f"The job names layer {layer}, and this checkpoint has {len(blocks)} blocks.")

    positive, negative = job["positivePrompts"], job["negativePrompts"]
    total = len(positive) + len(negative)
    torch.manual_seed(job["seed"])
    torch.cuda.manual_seed_all(job["seed"])
    torch.cuda.reset_peak_memory_stats()
    reader = _ResidualReader(blocks[layer])
    run.enter("deriving")
    started = time.perf_counter()
    done = 0

    def set_mean(prompts):
        nonlocal done
        means, norms = [], []
        for text in prompts:
            ids = tokenizer(text, return_tensors="pt").input_ids.to("cuda")
            model(ids, use_cache=False)
            mean, norm = reader.take()
            means.append(mean)
            norms.append(norm)
            done += 1
            run.progress_seen = True
            emit("progress", step=done, totalSteps=total, vramBytes=torch.cuda.memory_allocated())
        return torch.stack(means).mean(0), sum(norms) / len(norms)

    try:
        with torch.inference_mode():
            positive_mean, positive_norm = set_mean(positive)
            negative_mean, negative_norm = set_mean(negative)
        torch.cuda.synchronize()
        seconds = time.perf_counter() - started
    finally:
        reader.remove()

    run.enter("saving")
    reference_norm = (positive_norm + negative_norm) / 2
    vector, difference_norm = steering_vector(positive_mean.tolist(), negative_mean.tolist(), reference_norm)
    # On the CPU generator, so the control does not depend on the GPU's random state or on the prompts.
    noise = torch.randn(
        len(vector), generator=torch.Generator("cpu").manual_seed(job["seed"]), dtype=torch.float32
    ).tolist()
    random_vector = scaled_to_norm(noise, vector_norm(vector))
    with open(job["vectorPath"], "wb") as handle:
        handle.write(control_vector_bytes(layer, vector))
    with open(job["randomVectorPath"], "wb") as handle:
        handle.write(control_vector_bytes(layer, random_vector))
    write_result(
        job,
        {
            "seconds": round(seconds, 3),
            **measured_facts(model, blocks),
            "steering": {
                "layer": layer,
                "layerKind": layer_kind(getattr(blocks[layer], "block_type", None)),
                "referenceNorm": reference_norm,
                "differenceNorm": difference_norm,
                "vectorNorm": vector_norm(vector),
                "randomSeed": job["seed"],
                "loadSeconds": round(load_seconds, 3),
            },
        },
    )
    return False


def _class_location(cls, relative):
    """Where a module class is defined, or all None when it is not in the transformers or torch package."""
    try:
        if file := relative(inspect.getsourcefile(cls)):
            lines, start = inspect.getsourcelines(cls)
            forward = inspect.getsourcelines(cls.forward)[1] if "forward" in cls.__dict__ else None
            return {"file": file, "classStart": start, "classEnd": start + len(lines) - 1, "forwardLine": forward}
    except TypeError, OSError:
        pass
    return {"file": None, "classStart": None, "classEnd": None, "forwardLine": None}


def _block_structure(layer_types, index, block):
    """Blocks with the same key export the same graph."""
    return (
        layer_types[index] if layer_types else None,
        tuple((name, type(module).__name__) for name, module in block.named_modules()),
        tuple((name, tuple(parameter.shape), str(parameter.dtype)) for name, parameter in block.named_parameters()),
    )


def _cut_to_representatives(model, block_path, block_list, chosen, layer_types):
    """Swaps in the chosen blocks (the originals, so each keeps its layer_idx) and patches the config to match.

    False, with nothing changed, when layer_types cannot follow the cut: the export then runs at full depth.
    """
    decoder = model.config.get_text_config(decoder=True)
    depth = decoder.num_hidden_layers
    decoder.num_hidden_layers = len(chosen)
    try:
        if layer_types:
            decoder.layer_types = [layer_types[i] for i in chosen]
    except AttributeError:  # jamba and falcon_h1 derive layer_types through read-only properties
        pass
    if not layer_types_follow_cut(layer_types, chosen, getattr(decoder, "layer_types", None)):
        decoder.num_hidden_layers = depth
        try:
            if layer_types:
                decoder.layer_types = layer_types
        except AttributeError:
            pass
        return False
    parent, _, attribute = block_path.rpartition(".")
    setattr(model.get_submodule(parent), attribute, torch.nn.ModuleList([block_list[i] for i in chosen]))
    return True


def _anchor_modeling_functions(model):
    """Export records only frames named forward; anchoring keeps lines inside the modeling file's free functions."""
    modeling = sys.modules[type(model).__module__]
    functions = [
        inspect.unwrap(value)  # hub-kernel decorators wrap the reference function
        for value in vars(modeling).values()
        if inspect.isfunction(value) and value.__module__ == modeling.__name__
    ]
    try:  # a private torch hook: gone or changed means call-site lines
        from torch.fx.proxy import _register_stack_trace_anchor as register
    except ImportError:
        register = None
    return register_anchors(register, functions)


def _sha256(path):
    with open(path, "rb") as handle:
        return hashlib.sha256(handle.read()).hexdigest()


def source(job, run):
    """Indexes a model's PyTorch module tree and op graph from its config.json, on the meta device. No torch.cuda."""
    run.enter("loading")
    directory = job["configDirectory"]
    raw = read_raw_config(directory)  # the remote-code refusal, before transformers sees the directory
    from transformers import AutoConfig
    from transformers.models.auto.configuration_auto import CONFIG_MAPPING_NAMES
    from transformers.models.auto.modeling_auto import MODEL_FOR_CAUSAL_LM_MAPPING_NAMES

    unsupported = SourceRefusalError(
        "unsupported_model_type", "The installed transformers has no causal-LM model for it."
    )
    model_type = raw.get("model_type")
    if not isinstance(model_type, str) or model_type not in CONFIG_MAPPING_NAMES:
        raise unsupported
    # A local directory fixed by the host, offline, so there is no revision to pin.
    config = AutoConfig.from_pretrained(directory, local_files_only=True, trust_remote_code=False)  # nosec B615
    text_config = config.get_text_config(decoder=True)
    # A multimodal config whose own type has no causal-LM class builds its text model.
    build_config = config if config.model_type in MODEL_FOR_CAUSAL_LM_MAPPING_NAMES else text_config
    if build_config.model_type not in MODEL_FOR_CAUSAL_LM_MAPPING_NAMES:
        raise unsupported
    layer_types = list(getattr(build_config.get_text_config(decoder=True), "layer_types", None) or [])
    with torch.device("meta"):
        model = AutoModelForCausalLM.from_config(build_config, trust_remote_code=False).eval()

    block_path = discover_block_path(
        [
            (name, len(module))
            for name, module in model.named_modules()
            if isinstance(module, torch.nn.ModuleList)
            and len(module)
            and all(next(item.children(), None) is not None for item in module)
        ]
    )
    if block_path is None:
        raise SourceRefusalError("no_blocks", "The model has no list of repeated blocks.")
    block_list = model.get_submodule(block_path)

    roots = (os.path.dirname(transformers.__file__), os.path.dirname(torch.__file__))
    relative = functools.cache(lambda path: relative_source_file(path, *roots))
    located = {}
    blocks = [
        {
            "index": index,
            "path": f"{block_path}.{index}",
            "class": type(block).__name__,
            "layerType": layer_types[index] if index < len(layer_types) else None,
            "graphId": None,
            "modules": [],
        }
        for index, block in enumerate(block_list)
    ]
    outside = []
    for name, module in model.named_modules():
        if not name:
            continue
        cls = type(module)
        if cls not in located:
            located[cls] = _class_location(cls, relative)
        index = block_index_of(name, block_path)
        (outside if index is None else blocks[index]["modules"]).append(
            {"path": name, "class": cls.__name__, **located[cls]}
        )

    run.enter("exporting")
    chosen, position_of = representatives(
        [_block_structure(layer_types, index, block) for index, block in enumerate(block_list)]
    )
    representative = _cut_to_representatives(model, block_path, block_list, chosen, layer_types)
    if not representative:
        position_of = list(range(len(block_list)))
    anchored = _anchor_modeling_functions(model)

    graphs, graph_of, export_error = [], {}, None
    try:
        ids = torch.zeros((1, 8), dtype=torch.long, device="meta")
        program = torch.export.export(model, (ids,), kwargs={"use_cache": False}, strict=False)
    except Exception as error:  # degrades to the module index, with the reason
        export_error = sanitized_export_error(error)
    else:
        calls = [node for node in program.graph.nodes if node.op == "call_function"]
        position = {node: slot for slot, node in enumerate(calls)}
        ops = []
        for node in calls:
            file, line = op_location(node.meta.get("stack_trace"), relative)
            stack = node.meta.get("nn_module_stack") or {}
            target = str(node.target)
            ops.append(
                {
                    "path": list(stack.values())[-1][0] if stack else "",
                    "file": file,
                    "line": line,
                    "op": target.split(".")[1] if "." in target else target,
                    "inputs": [position[source] for source in node.all_input_nodes if source in position],
                }
            )
        graphs, graph_of = op_graphs(
            ops, block_path, lambda file, line: linecache.getline(source_file_path(file, *roots), line)
        )
    for block in blocks:
        block["graphId"] = graph_of.get(position_of[block["index"]])

    run.enter("saving")
    result = {
        "transformersVersion": transformers.__version__,
        "torchVersion": torch.__version__,
        "modelType": config.model_type,
        "textModelType": text_config.model_type if text_config is not config else None,
        "modelClass": type(model).__name__,
        "blockPath": block_path,
        "exportMode": "representative" if representative else "full",
        "anchoredLines": anchored,
        "blocks": blocks,
        "outside": outside,
        "outsideGraphId": graph_of.get(None),
        "graphs": graphs,
        "exportError": export_error,
        "files": [
            {"file": file, "sha256": _sha256(source_file_path(file, *roots))}
            for file in named_files(blocks, outside, graphs)
        ],
    }
    blob = serialized_result(result, job["maxResultBytes"])
    with open(job["resultPath"], "wb") as handle:
        handle.write(blob)
    return False


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--config", required=True)
    arguments = parser.parse_args()

    # What it writes holds the prompt's output and statistics: owner-only even if the work directory's mode failed.
    os.umask(0o077)
    run = _Run()
    signal.signal(signal.SIGTERM, run.on_sigterm)
    emit("handshake", contractVersion=CONTRACT_VERSION, torch=torch.__version__, transformers=transformers.__version__)
    run.start_heartbeat()
    try:
        with open(arguments.config, encoding="utf-8") as handle:
            job = validate_job(json.load(handle))
        run_job = {STEERING_KIND: derive, SOURCE_KIND: source}.get(job["kind"], trace)
        cancelled = run_job(job, run)
    except JobError as error:
        emit("error", category="contract", message=str(error))
        return 1
    except SourceRefusalError as error:
        emit("error", category=error.category, message=str(error))
        return 1
    except torch.cuda.OutOfMemoryError:
        emit("error", category="out-of-memory", message="The GPU ran out of memory during the job.")
        return 1
    except Exception as error:
        emit("error", category="trace", message=describe_failure(error))
        return 1
    finally:
        run.finish()

    emit("done", cancelled=cancelled)
    return CANCELLED_EXIT_CODE if cancelled else 0


if __name__ == "__main__":
    sys.exit(main())

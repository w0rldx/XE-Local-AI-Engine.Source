"""Model Lab: reads chosen layers of a local safetensors checkpoint with forward hooks that change no value.

Three kinds of ``job.json``. A trace (``ModelLabTraceJobV1`` on the C# side) records statistics while generating; the
host fixes the layers, token count and byte cap, and may ask for the attention rows of full-attention blocks and for
the logit lens of every block. A
steering job (``ModelLabSteeringJobV1``) derives a control vector from two contrasting prompt sets and writes it with
a random one of the same norm. A source job
(``ModelLabSourceJobV1``) indexes the PyTorch code of a model from its config.json alone, on the meta device: no
weights, no GPU. Nothing in a job can name code to run. Offline, no remote code, no unsloth.

The protocol is the trainer's JSON-lines shape with its own contract version, on stdout:

    {"event": "handshake", "contractVersion": 5, ...versions}
    {"event": "phase",     "phase": "loading" | "tracing" | "deriving" | "exporting" | "saving"}
    {"event": "progress",  "step", "totalSteps", "vramBytes"}    a token, or a contrast prompt
    {"event": "heartbeat", "phase"}          until the first progress, and while saving; progress proves life between
    {"event": "done",      "cancelled": bool}
    {"event": "error",     "category", "message"}

Exit status: 0 on success, 3 on SIGTERM in any phase, 1 on any error.
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
from transformers import (
    AutoModelForCausalLM,
    AutoTokenizer,
    DynamicCache,
    LogitsProcessor,
    LogitsProcessorList,
    StoppingCriteria,
    StoppingCriteriaList,
)

from modellablib import (
    ATTENTION_WEIGHT_SCALE,
    CONTRACT_VERSION,
    SOURCE_KIND,
    STEERING_KIND,
    CappedTraceWriter,
    JobError,
    SourceRefusalError,
    attention_record,
    block_index_of,
    captures_attention,
    check_aligned,
    competition_rank,
    control_vector_bytes,
    describe_failure,
    discover_block_path,
    fitted_trace_result,
    heartbeat_counts,
    layer_kind,
    layer_types_follow_cut,
    lens_readable,
    lens_record,
    named_files,
    op_graphs,
    op_location,
    out_of_range_layers,
    read_raw_config,
    records_lens,
    register_anchors,
    relative_source_file,
    representatives,
    sanitized_export_error,
    scaled_to_norm,
    scored_token,
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
TOP_CANDIDATES = 20


def emit(event, **fields):
    # os.write, not print: the SIGTERM handler and the heartbeat thread both write here, and one write is one line.
    os.write(1, (json.dumps({"event": event, **fields}) + "\n").encode())


class _Run:
    """What the signal handler, the heartbeat and the stopping criterion share."""

    def __init__(self):
        self.phase = "loading"
        self.progress_seen = False
        self._finished = threading.Event()

    def enter(self, phase):
        self.phase = phase
        emit("phase", phase=phase)

    def on_sigterm(self, _signum, _frame):
        # Exits in every phase: a job writes its result only at its end, so a cancelled one has nothing worth keeping.
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


class _AttentionRows:
    """The attention of each predicting query in the chosen full-attention blocks, recomputed in float32 beside the
    kernel, whose result is returned unchanged. Wraps the "sdpa" entry, which every attention module resolves per call.
    """

    def __init__(self, chosen, prompt_tokens, top_keys):
        from transformers.modeling_utils import ALL_ATTENTION_FUNCTIONS
        from transformers.models.qwen3_5.modeling_qwen3_5 import Qwen3_5Attention

        self.rows = []
        self._steps = dict.fromkeys(chosen, -1)
        self._prompt_tokens = prompt_tokens
        self._top_keys = top_keys
        self._module_class = Qwen3_5Attention
        self._functions = ALL_ATTENTION_FUNCTIONS
        self._kernel = ALL_ATTENTION_FUNCTIONS["sdpa"]
        ALL_ATTENTION_FUNCTIONS["sdpa"] = self._wrapper

    def _wrapper(self, module, query, key, value, attention_mask, **kwargs):
        result = self._kernel(module, query, key, value, attention_mask, **kwargs)
        # The vision tower's attention shares the registry and has no layer_idx.
        if isinstance(module, self._module_class) and module.layer_idx in self._steps:
            self._capture(module, query, key)
        return result

    def _capture(self, module, query, key):
        layer = module.layer_idx
        self._steps[layer] += 1
        step = self._steps[layer]
        length = key.shape[2]
        # A static cache hands over its whole buffer and a chunked prefill one chunk: rows would sit at wrong positions.
        if query.shape[0] != 1 or length != self._prompt_tokens + step:
            raise JobError(
                f"Block {layer} saw {length} keys at step {step}, not the prompt's {self._prompt_tokens} plus the "
                "step: the cache or prefill is not the one attention capture needs."
            )
        heads, kv_heads = query.shape[1], key.shape[1]
        # Query head h reads KV head h // groups: the queries are grouped instead of expanding the key history.
        last = query[0, :, -1].float().view(kv_heads, heads // kv_heads, -1)
        scores = torch.matmul(last, key[0].float().transpose(1, 2)) * module.scaling
        weights = torch.softmax(scores, dim=-1).view(heads, length)
        top = weights.topk(min(self._top_keys, length), dim=-1)
        # Kept on the GPU until saving, like the candidates: no host sync per step.
        self.rows.append((step, layer, length - 1, top.indices.to(torch.int32), top.values))

    def recorded(self):
        return [
            attention_record(step, layer, position, keys.tolist(), weights.tolist())
            for step, layer, position, keys, weights in self.rows
        ]

    def remove(self):
        # Assignment wrote the instance's local mapping only; deleting it brings back the kernel.
        del self._functions["sdpa"]


class _Progress(StoppingCriteria):
    """Reports each token. It never stops generation: SIGTERM exits the process instead."""

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
        return torch.zeros((input_ids.shape[0],), device=input_ids.device, dtype=torch.bool)


def blocks_of(model):
    inner = model.model
    return inner.language_model.layers if hasattr(inner, "language_model") else inner.layers


def final_norm_of(model):
    inner = model.model
    return getattr(inner.language_model if hasattr(inner, "language_model") else inner, "norm", None)


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
        "cuda": torch.version.cuda,
    }


def write_result(job, result):
    with open(job["resultPath"], "w", encoding="utf-8") as handle:
        json.dump(result, handle, ensure_ascii=False)


class _Candidates(LogitsProcessor):
    """Each step's top TOP_CANDIDATES and chosen token, raw and processed, taken as the step runs: no full-vocabulary
    tensor outlives its step. A forward hook on the model reads the raw logits; the processor returns scores unchanged.
    """

    def __init__(self, model):
        self.steps = []
        self._raw = None
        self._handle = model.register_forward_hook(self._read_raw)

    def _read_raw(self, _module, _args, output):
        # What _sample copies into next_token_logits: the model's output, softcapping included, last position.
        self._raw = torch.log_softmax(output.logits[0, -1].float(), dim=-1)

    def __call__(self, input_ids, scores):
        # Merged after the generation-config processors, so these are the scores the greedy argmax reads.
        raw, self._raw = self._raw, None
        if raw is None:
            raise RuntimeError("A generation step ran its processors without a model forward before them.")
        chosen = torch.argmax(scores[0])
        processed = torch.log_softmax(scores[0], dim=-1)
        top = functools.partial(torch.topk, k=TOP_CANDIDATES)

        # A copy, not raw[chosen]: a 0-d tensor index syncs the host and returns a view holding the whole vector.
        def at_chosen(logprobs):
            return logprobs.index_select(0, chosen.view(1)).squeeze(0)

        # Kept on the GPU, a few hundred bytes a step that own their storage, so recording adds no host sync.
        self.steps.append((chosen, at_chosen(raw), *top(raw), at_chosen(processed), *top(processed)))
        return scores

    def recorded(self):
        """Per step (chosen id, raw logprob, logprobs, ids, processed logprob, logprobs, ids), as Python values."""
        return [[value.tolist() for value in step] for step in self.steps]

    def remove(self):
        self._handle.remove()


class _Lens(LogitsProcessor):
    """Each step's logit lens: every block's newest state read through the model's own final norm and output layer.

    Blocks 0..B-2 in one batched readout a step; the last block's readout is the model's raw logits, which that
    readout is by construction (a batched one is not bit for bit). Clone-only hooks; the results stay on the GPU
    until saving, so no step syncs the host. The processor returns scores unchanged.
    """

    def __init__(self, model, blocks, top, norm, head):
        self.steps = []
        self._top = top
        self._count = len(blocks) - 1
        self._hidden = model.config.get_text_config(decoder=True).hidden_size
        self._norm = norm
        self._head = head
        self.vocab_size = self._head.out_features
        self.tied_embeddings = self._head.weight.data_ptr() == model.get_input_embeddings().weight.data_ptr()
        self._stash = {}
        self._raw = None
        self._handles = [blocks[i].register_forward_hook(self._hook(i)) for i in range(self._count)]
        self._handles.append(model.register_forward_hook(self._read_raw))

    def _hook(self, index):
        def run(_module, _args, output):
            hidden = output[0] if isinstance(output, tuple) else output
            if hidden.dim() != 3 or hidden.shape[0] != 1 or hidden.shape[2] != self._hidden:
                raise RuntimeError(f"Block {index} returned {tuple(hidden.shape)}, not (1, tokens, {self._hidden}).")
            # A clone: a view would keep the block's whole prefill output alive. Overwritten, so a chunked prefill
            # keeps its last chunk's last position.
            self._stash[index] = hidden[0, -1].clone()

        return run

    def _read_raw(self, _module, _args, output):
        self._raw = output.logits[0, -1]

    def __call__(self, input_ids, scores):
        raw, self._raw = self._raw, None
        if raw is None or len(self._stash) != self._count:
            missing = [i for i in range(self._count) if i not in self._stash]
            raise RuntimeError(f"A lens step ran without the model's logits or the states of blocks {missing}.")
        chosen = torch.argmax(scores[0])
        states = torch.stack([self._stash[i] for i in range(self._count)])
        self._stash.clear()
        logits = torch.cat([self._head(self._norm(states)), raw.view(1, -1)])
        logprobs = torch.log_softmax(logits, -1, dtype=torch.float32)
        top = logprobs.topk(self._top, dim=-1)
        at = logprobs.index_select(1, chosen.view(1))
        # In nats, over the whole vocabulary, in place: one [B, V] buffer. nansum drops a masked token's 0 * -inf.
        entropy = -(logprobs.exp().mul_(logprobs)).nansum(-1)
        # nansum would make a NaN row finite. log_softmax turns a row with a NaN or +inf logit (or all -inf) wholly
        # NaN, so its first column tells; such a row is written null.
        entropy = torch.where(torch.isnan(logprobs[:, 0]), torch.nan, entropy)
        self.steps.append((chosen, top.indices, top.values, at.squeeze(1), competition_rank(logprobs, at), entropy))
        return scores

    def recorded(self):
        """Per step (chosen id, ids [B][N], logprobs [B][N], chosen logprobs [B], ranks [B], entropies [B])."""
        return [[value.tolist() for value in step] for step in self.steps]

    def remove(self):
        for handle in self._handles:
            handle.remove()


def trace(job, run):
    tokenizer, model = load_model(job, run)
    blocks = blocks_of(model)
    if outside := out_of_range_layers(job["layers"], len(blocks)):
        raise JobError(f"The job names layers {outside}, and this checkpoint has {len(blocks)} blocks.")
    capture = captures_attention(job)
    attention_layers = job["attentionLayers"] if capture else []
    if other := [
        i for i in attention_layers if i >= len(blocks) or getattr(blocks[i], "block_type", None) != "full_attention"
    ]:
        raise JobError(f"The job asks for attention in blocks {other}, which are not full-attention blocks.")
    kernel = model.config._attn_implementation
    if capture and kernel != "sdpa":
        raise JobError(f"Attention is captured beside the sdpa kernel, and this model runs {kernel!r}.")

    ids = prompt_ids(tokenizer, job["prompt"]).to("cuda")
    prompt_tokens = ids.shape[1]
    total = job["maxNewTokens"]
    torch.manual_seed(job["seed"])
    torch.cuda.manual_seed_all(job["seed"])
    torch.cuda.reset_peak_memory_stats()

    writer = CappedTraceWriter(job["tracePath"], job["traceByteCap"])
    tracer = _Tracer(blocks, job["layers"], writer)
    candidates = _Candidates(model)
    lens = None
    if records_lens(job):
        norm, head = final_norm_of(model), model.get_output_embeddings()
        if lens_readable(len(blocks), norm, head, model.config.get_text_config(decoder=True)):
            lens = _Lens(model, blocks, job["lensTopTokens"], norm, head)
    attention = _AttentionRows(attention_layers, prompt_tokens, job["attentionTopKeys"]) if capture else None
    # Pinned with capture on: a checkpoint's static cache or chunked prefill would break the position check.
    pinned_cache = (
        {
            "past_key_values": DynamicCache(config=model.config.get_text_config(decoder=True)),
            "cache_implementation": None,
            "prefill_chunk_size": None,
        }
        if capture
        else {}
    )
    run.enter("tracing")
    started = time.perf_counter()
    try:
        with torch.inference_mode():
            sequences = model.generate(
                ids,
                attention_mask=torch.ones_like(ids),
                do_sample=False,
                num_beams=1,  # a checkpoint's beam setting would break the step alignment below
                max_new_tokens=total,
                logits_processor=LogitsProcessorList([candidates] + ([lens] if lens else [])),
                # Explicit, because a checkpoint's generation_config fills what is not passed: a tensor result, and
                # no per-step scores, logits, attentions or hidden states kept until generate returns.
                return_dict_in_generate=False,
                output_scores=False,
                output_logits=False,
                output_attentions=False,
                output_hidden_states=False,
                use_cache=True,
                eos_token_id=stop_tokens(tokenizer, model.generation_config.eos_token_id),
                pad_token_id=tokenizer.eos_token_id,
                stopping_criteria=StoppingCriteriaList([_Progress(run, prompt_tokens, total)]),
                # A checkpoint's watermark would change the scores after every processor passed here.
                watermarking_config=None,
                **pinned_cache,
            )
        torch.cuda.synchronize()
        seconds = time.perf_counter() - started
    finally:
        tracer.remove()
        candidates.remove()
        if lens:
            lens.remove()
        if attention:
            attention.remove()
    run.enter("saving")
    generated = sequences[0, prompt_tokens:].tolist()
    steps = candidates.recorded()
    check_aligned([step[0] for step in steps], generated)
    lens_steps = lens.recorded() if lens else []
    if lens:
        check_aligned([step[0] for step in lens_steps], generated, "lens")
    if attention and len(attention.rows) != len(generated) * len(attention_layers):
        raise RuntimeError(
            f"Attention was captured {len(attention.rows)} times for {len(generated)} tokens in "
            f"{len(attention_layers)} blocks: a step ran past the wrapped kernel."
        )

    def texts(token_ids):
        return [tokenizer.decode([token]) for token in token_ids]

    tokens = [
        scored_token(
            token,
            tokenizer.decode([token]),
            (raw_logprob, raw_ids, texts(raw_ids), raw_logprobs),
            (logprob, processed_ids, texts(processed_ids), processed_logprobs),
        )
        for token, (_, raw_logprob, raw_logprobs, raw_ids, logprob, processed_logprobs, processed_ids) in zip(
            generated, steps, strict=True
        )
    ]
    result = {
        "promptTokens": prompt_tokens,
        "tokens": tokens,
        "text": tokenizer.decode(generated),
        "seconds": round(seconds, 3),
        **measured_facts(model, blocks),
        "layers": job["layers"],
        "seed": job["seed"],
        "maxNewTokens": total,
        "promptPieces": [{"id": token, "text": tokenizer.decode([token])} for token in ids[0].tolist()],
        "layerKinds": "".join(layer_kind(getattr(block, "block_type", None)) for block in blocks),
        "attnImplementation": kernel,
    }
    if lens:
        # After the hook records, under its own cap and footer: neither part's overflow touches the other's counts.
        lens_writer = writer.section(job["lensByteCap"], "lensFooter")
        decoded = {}
        for step, (_, top_ids, top_logprobs, chosen, ranks, entropies) in enumerate(lens_steps):
            for token in {token for row in top_ids for token in row} - decoded.keys():
                decoded[token] = tokenizer.decode([token])
            lens_writer.write(lens_record(step, top_ids, top_logprobs, chosen, ranks, entropies, decoded))
        lens_writer.close()
        result["lens"] = {
            "layers": list(range(len(blocks))),
            "topTokens": job["lensTopTokens"],
            "records": lens_writer.records,
            "truncated": lens_writer.truncated,
            "tiedEmbeddings": lens.tied_embeddings,
            "vocabSize": lens.vocab_size,
        }
    writer.close()
    if attention:
        attention_writer = CappedTraceWriter(job["attentionPath"], job["attentionByteCap"])
        for record in attention.recorded():
            attention_writer.write(record)
        attention_writer.close()
        text_config = model.config.get_text_config(decoder=True)
        result["attention"] = {
            "layers": attention_layers,
            "heads": text_config.num_attention_heads,
            "kvHeads": text_config.num_key_value_heads,
            "headDim": text_config.head_dim,
            "topKeys": job["attentionTopKeys"],
            "weightScale": ATTENTION_WEIGHT_SCALE,
            "records": attention_writer.records,
            "truncated": attention_writer.truncated,
        }
    with open(job["resultPath"], "wb") as handle:
        handle.write(fitted_trace_result(result))


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
        run_job(job, run)
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

    emit("done", cancelled=False)
    return 0


if __name__ == "__main__":
    sys.exit(main())

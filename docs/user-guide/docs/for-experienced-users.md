# For people who already run local models

If you already use llama.cpp, Ollama or LM Studio, the rest of these docs are written for someone who
doesn't — this page isn't. It's the specifics, including the parts that aren't implemented.

Everything here was verified against the shipped build. Where I don't know, it says so rather than
guessing.

---

## The 60-second version

| | |
|---|---|
| **Engine** | llama.cpp, pinned to upstream tag **`b10201`** |
| **Backends** | CUDA (Windows), Vulkan (Windows/Linux), CPU |
| **Runtime integrity** | Each binary pinned to an exact upstream asset + **SHA-256 verified before extraction**; mismatch aborts |
| **Default context** | **8192 tokens** |
| **Launch flags** | Benchmarked per machine, then frozen — **not visible, not editable**. A developer-mode-only panel can append extra raw flags per llama.cpp model — see below |
| **BYO binary** | `XE_LLAMACPP_SERVER_PATH` (env var only) |
| **Import existing GGUFs** | **Supported** — one file at a time, copied into the managed directory. Packaged desktop app only. [Details](#using-ggufs-you-already-have) |
| **Local API** | `/api/local/v1`, loopback-only, OpenAPI at `/openapi/local/v1/…` |
| **OpenAI-compatible surface** | Opt-in inbound proxy at `/api/local/v1/proxy/v1`, bearer-key gated, loopback-only |
| **MCP** | Both directions — outbound to servers you register, and an inbound server surface |
| **Fine-tuning** | QLoRA, **Linux + NVIDIA only**, in a pinned uv-managed Python runtime. [Details](#fine-tuning-training) |
| **Benchmarks** | One frozen task, many models; KV-cache type per run, launch receipts, optional 1–5 judge. [Details](#benchmarks) |
| **Multi-GPU / tensor split** | Unverified — ask me |

---

## Runtime and acceleration

The app manages llama.cpp itself; you don't install a server. Binaries are pinned per
(OS, arch, backend) with their upstream asset name **and SHA-256**, verified before extraction — a
mismatch aborts rather than running.

**Windows** gets a prebuilt CUDA runtime (`llama-b10201-bin-win-cuda-12.4-x64.zip`) or Vulkan.

**Linux** has no upstream prebuilt CUDA asset, so Vulkan is the default. There's an in-app
**build-from-source** path that compiles a CUDA runtime, pinned to an exact upstream commit and
refusing to proceed if the checked-out source doesn't match. That path needs testing across distros
and driver versions — it's one of the more valuable things to try.

### Bring your own binary

```
XE_LLAMACPP_SERVER_PATH=/path/to/your/llama-server
```

Environment variable only — there's no UI for it. This is the escape hatch if you want a build the app
doesn't ship. Note the app validates the override and will reject it (with a diagnostic) if the binary
can't enumerate devices for the selected backend.

### Ports

| | |
|---|---|
| App UI | `127.0.0.1`, **OS-assigned port** (binds `:0`, so it differs every machine and every fresh install) |
| llama-server | `18100`–`18199` |
| stable-diffusion.cpp | `18200`–`18299` |

The two runtime ranges are deliberately offset so they never contend. All loopback.

---

## Launch flags — the honest answer

The app explores and **benchmarks** launch settings for a model on your specific machine, then freezes
that profile and replays it, so throughput doesn't drift between runs. This is measured, not a
heuristic.

**But:** the Inference Profiles panel (on **Models → Recommendations**) reports *outcomes only* —
tokens/second and VRAM. It does **not** show the resulting flags, and there is **no UI to edit them**.

So if you're used to hand-tuning `-ngl`, `-c`, `--tensor-split` or flash-attention settings: you cannot
change what the frozen profile decided. Two levers exist around it:

- `XE_LLAMACPP_SERVER_PATH`, which swaps the whole binary rather than the arguments.
- A per-model **extra arguments** override — space-separated raw flags appended when that model loads.
  It lives on an Advanced tab in the model details dialog, and that tab only renders in **developer
  mode** and only for llama.cpp models. The model path, host and port stay app-managed. An invalid flag
  stops that one model from starting; clearing the override restores the defaults.

If that's still a dealbreaker, say so — it's exactly the kind of feedback that decides what gets built next.

---

## Models

Stored flat as `.gguf` under:

```
%LOCALAPPDATA%\XE-Local-AI-Engine\models\        (Windows)
~/.local/share/XE-Local-AI-Engine/models/        (Linux)
```

Discovery and download are from Hugging Face, in-app. Per-quant hardware fit is computed against
measured free VRAM rather than a static table, which is the main thing the model picker does that a
plain file list doesn't.

### Using GGUFs you already have

**Models → Installed → Import model** takes the absolute path to one `.gguf` file, previews what it
found, then copies it into the managed directory. Still **no scan-a-folder setting**, and dropping
files into `models/` yourself is still **not** reliably picked up: the registry is manifest-driven, and
the directory rescan is a *recovery* path that only runs when the manifest can't be read (and it's
top-directory-only). A symlink or junction has the same problem — the manifest, not the filesystem, is
the source of truth. The import path is what writes the manifest entry, which is why it exists.

The flow is preview → confirm:

1. Paste an absolute path. The file is opened without following symlinks/reparse points, and a source
   inside the managed models directory is refused.
2. The header is parsed strictly and the file is classified. You get the GGUF version, architecture,
   size, detected quantization, and a proposed model name.
3. You confirm (or edit) the base name and quantization. The result is named `basename:QUANT`.
4. The file is **copied** — SHA-256 hashed as it goes — into `models/`, re-inspected after the copy,
   then committed together with its sidecar metadata file and its registry entry. A failure part-way
   through rolls the whole thing back rather than leaving a half-registered model.

Practical consequences:

- **Copy, not move or link.** Your original stays where it is, so you need free space for a second
  full copy plus a margin. The preview tells you up front if you don't have it.
- **One standalone chat model per import.** Split/sharded GGUFs (`…-00001-of-00003.gguf`) are refused,
  as are embedding models, rerankers, bare LoRA adapters, and `mmproj` projectors. Architecture is
  checked against an allow-list of causal families (llama, mistral, mixtral, qwen2/qwen3/qwen35 and
  their MoE variants, gemma/gemma2/gemma3, phi2/phi3/phi3moe, deepseek2, command-r, cohere2, gpt2,
  gptneox, starcoder2, internlm2). GGUF versions 2 and 3 only.
- **No projector comes with it**, so an imported vision model is text-only. The `mmproj` companion is
  only fetched on the in-app Hugging Face download path.
- **The preview is bound to the exact bytes.** It expires after 10 minutes, and if the file changes in
  between you're told to preview it again.
- **Packaged desktop app only.** The capability endpoint reports unavailable outside desktop mode, and
  the Import button doesn't render — so this is available in the Portable ZIP / AppImage / installed
  builds, and not in a bare server launch.

An imported model is tagged **Imported** in the model list and everywhere origin is shown.

If you have a 200 GB library, you're still importing it one file at a time — bulk import is worth
asking for if you need it.

### VRAM measurement gap

On **AMD and Intel GPUs under Windows**, the app runs inference on the GPU via Vulkan but **cannot read
VRAM**, so fit recommendations fall back to sizing from system RAM. Expect less precision there; step
down a size or quant if a recommendation won't load. NVIDIA is measured properly.

---

## The RAG pipeline

Since "hybrid search with reranking" describes nothing, here are the actual defaults:

| | |
|---|---|
| Embedding model | **`nomic-embed-text`**, served through the local llama.cpp provider |
| Chunking | **≤2000 chars / ≤512 tokens**, **200-char overlap**, header-boundary aware |
| Lexical index | SQLite **FTS5**, **BM25** ranking |
| Vector search | Local embeddings, cosine similarity |
| Fusion | **Reciprocal Rank Fusion** |
| Reranker | **Off by default** (no model configured); cross-encoder supported when set |
| Embedding batch | 64 |
| Query-embedding cache | 128 entries, 300 s TTL |
| Degraded mode | Falls back to lexical-only if embeddings are unavailable, rather than failing |

**Not configurable from the UI** — these are options-bound defaults.

Extraction has **no OCR**, so scanned/image-only PDFs extract poorly.

> **Privacy specifics that matter here:** extracted chunk text, the FTS index, headings, section
> structure and the **embeddings themselves** are stored **unencrypted** — embeddings are recoverable
> enough to be treated as the text. [Details](privacy-and-data.md#what-is-encrypted-and-what-is-not)

---

## Data, state and security specifics

Everything lives in one directory (`%LOCALAPPDATA%\XE-Local-AI-Engine`), separate from the binaries.

- **Field-level encryption**, not whole-database: AES-256-GCM over sensitive columns. `node.key` is
  **DPAPI-wrapped to your Windows user account** — which is why disk theft doesn't yield your chats,
  and also why **a copied data folder will not open under another account or machine.** The app fails
  closed rather than pretending.
- **Single-instance lease** per data directory. A second launch refuses to start and exits — it can't
  race or corrupt the database.
- **Loopback enforced twice**: every request is checked for a loopback peer, and at startup the app
  inspects its actually-bound addresses and **shuts down with a non-zero exit** if any is routable
  (overridable only by an explicit operator setting).
- **Development Mode ships enabled but can be disabled.** Set `Development:Enabled=false` to remove
  its services and surface; when enabled, it only acts on a repository you register. It is **not an
  OS sandbox** — builds and scripts run as your user.
  [Read this before pointing it anywhere](privacy-and-data.md#development-mode-and-its-limits)
- **Custom tools are host-powerful and default off.** HTTP tools can reach configured network hosts;
  command tools launch a direct executable as your user. The node switch starts disabled, the built-in
  form initializes new definitions as disabled, and every invocation remains approval-wrapped. A fixed
  tool can reuse an explicit, version-bound session approval; a parameterized tool re-prompts for each
  model-selected argument set. The API persists an acknowledged caller's requested enablement.
- **MCP runs both directions.** Outbound to servers you register (they execute as you — same trust
  boundary as Dev Mode), plus an inbound server surface so external clients can drive the app.

---

## Advanced runtime boundaries

- **Multi-GPU launch arguments are profile-owned.** A frozen inference profile can replay explicit
  `-ngl`, `-ts` and `-ot` values. The normal automatic launch policy does not invent a tensor split.
- **The supported local app API is not an OpenAI-compatible API.** It is the engine's authenticated
  `/api/local/v1` REST and SignalR surface. The supervised `llama-server` child exposes an internal,
  loopback OpenAI-compatible `/v1` endpoint for provider adapters; that is not the public app contract.
- **GPU KV-cache quantization is automatic, not a chat-screen control.** GPU launches default to a
  matching `q8_0` K/V cache with flash attention, then record and use a safe non-quantized fallback if
  that optimized combination is unsupported. CPU launches keep the non-quantized path. Frozen
  inference profiles may replay explicit KV types.

---

## Benchmarks

**Benchmarks** (top-level sidebar entry) is not a token-throughput toy: a *project* freezes one task
and one agent, and every *run* answers that same frozen task with a different model, so the runs stay
comparable. A project becomes uneditable once it has runs — delete its terminal runs to edit it again.

| | |
|---|---|
| Unit of comparison | One frozen core task + agent + requested context, per project |
| Run queue | Durable — queued work survives a node restart; a phase that was *running* when the node stopped is reported as interrupted rather than silently retried. The UI falls back to durable HTTP state when the live connection drops |
| Context | Runs are refused if the model can't provide the requested context, rather than quietly truncating |
| Model identity | The installed model's content fingerprint is frozen with the run; if the model changes afterwards, the run says so |
| **KV-cache type** | Chosen **per run**: `Auto`, or an explicit type. Auto uses `q8_0` on GPU when the selected binary supports it, otherwise `f16`. Quantized types launch with flash attention on |
| Evidence | Every run stores a **launch receipt** and an **environment** snapshot, with *intended* vs *effective* shown side by side and any differing fields called out |
| Scoring | Your own 1–5 score, plus an optional **automated judge** — a second model, queued separately, that only runs after a successful primary run and returns a 1–5 score with a rationale. A failed judge never invalidates the primary result |

The judge's reply is constrained to a fixed JSON shape (`schemaVersion`, `score`, `rationale`) and a
score outside 1–5 is rejected, so a rambling judge fails loudly instead of scoring silently.

---

## Fine-tuning (Training)

The **Training** sidebar group is a real QLoRA fine-tuning pipeline, not a wrapper around a cloud API.
It is **Linux + NVIDIA only** and it takes the whole GPU while a run is going — no chat, image or
benchmark work happens at the same time.

**Prerequisites**, all checked before anything is installed: Linux host, an NVIDIA driver (probed via
`nvidia-smi`), **≥ 20 GB free disk**, **≥ 16 GB system RAM**, and the pinned lockfile. Fine-tuning runs
in a **pinned, uv-managed Python environment** installed once and reused (measured at roughly 7.5 GB;
the 20 GB figure is peak install, which parks the previous venv until the swap succeeds).

The pipeline:

1. **Datasets** — a *definition* names a node-local **teacher model** (cloud teachers are refused),
   system instructions, the tools the teacher is offered (schemas snapshotted on save), sample kinds
   with target counts, hold-out share (5–30%), teacher temperature, an optional base seed, and an
   optional **critic model** that scores every sample. Cap: 2000 samples per definition. Teacher output
   is either constrained-decoded or validated after generation. Every sample is reviewable and
   individually approvable/rejectable, and any generated call to anything but an approval-free
   read-only tool needs a **tool mock**.
2. **Runs** — one dataset + one base checkpoint. Base checkpoints are **safetensors from Hugging Face**,
   not GGUFs: you cannot fine-tune a quantized copy. The wizard estimates VRAM against what's actually
   free and refuses a run that doesn't fit, and it makes you confirm you may fine-tune those weights
   (recording the fact when the repo declares no licence). Knobs: sequence length, batch size, LoRA
   rank, epochs. Live loss and step progress stream while it runs.
3. **Export** — merged GGUF at a chosen quantization, or adapter-only (always F16, served on top of the
   base model it trained against). Export runs merge → convert → quantize → inspect → **smoke test**.
4. **Register as model** — a passing artifact becomes an installed local model, with the quantization
   appended to the name and the base checkpoint plus dataset fingerprint recorded on the entry.
5. **Comparisons** — score the base model and the tuned model on the **same frozen hold-out samples**
   and read the per-sample-kind accuracy delta, optionally paired with two benchmark runs for
   tokens/second and duration deltas. If the dataset was edited after a run or evaluation froze its
   hold-out set, the report says the numbers may not be comparable rather than hiding it.

---

## The most useful things you can test

You have hardware and habits I don't:

1. **The Linux CUDA source build** — across distros, drivers and CUDA versions. Least-covered path.
2. **AMD/Intel via Vulkan**, especially whether inference actually lands on the GPU (the app can't yet
   warn you reliably if it silently doesn't).
3. **Whether the frozen inference profile holds up** on your box, or whether you'd beat it by hand.
4. **The RAG defaults** against a corpus you know well — chunk size and the absence of reranking by
   default are the obvious suspects.

Blunt technical feedback is more useful than polite feedback. → [How to send it](feedback.md)

---

**[← Back to the main page](../README.md)**

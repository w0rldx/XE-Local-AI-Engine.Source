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
| **Launch flags** | Benchmarked per machine, then frozen — **not visible, not editable** |
| **BYO binary** | `XE_LLAMACPP_SERVER_PATH` (env var only) |
| **Import existing GGUFs** | **Not supported** — see below |
| **Local API** | `/api/local/v1`, loopback-only, OpenAPI at `/openapi/local/v1/…` |
| **MCP** | Both directions — outbound to servers you register, and an inbound server surface |
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

So if you're used to hand-tuning `-ngl`, `-c`, `--tensor-split` or flash-attention settings: you can't,
in this build. The only lever is `XE_LLAMACPP_SERVER_PATH`, which swaps the whole binary rather than
the arguments.

If that's a dealbreaker, say so — it's exactly the kind of feedback that decides what gets built next.

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

### Using GGUFs you already have — not supported

> **There is no import path in this build.** No import endpoint, no scan-a-folder setting.

Dropping files into `models/` is **not** reliably picked up: the registry is manifest-driven, and the
directory rescan is a *recovery* path that only runs when the manifest can't be read (and it's
top-directory-only). A symlink or junction has the same problem — the manifest, not the filesystem, is
the source of truth.

If you have a large existing GGUF library, **this is currently a real limitation**, and worth telling
me about — it's the single most likely reason someone with 200 GB of models bounces off this.

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
- **Development Mode ships enabled.** There's no switch to leave off; the gate is that it only acts on
  a repository you register. It is **not an OS sandbox** — builds and scripts run as your user.
  [Read this before pointing it anywhere](privacy-and-data.md#development-mode-and-its-limits)
- **MCP runs both directions.** Outbound to servers you register (they execute as you — same trust
  boundary as Dev Mode), plus an inbound server surface so external clients can drive the app.

---

## What I couldn't verify for you

I'd rather leave these open than guess:

- **Multi-GPU / `--tensor-split` behaviour**
- **Whether the local API is OpenAI-compatible** enough to point existing tooling at (it's the app's
  own OpenAPI surface, not an `/v1/chat/completions` shim, as far as I can tell)
- **KV-cache quantization** controls
- **Speculative decoding** — there's draft-model handling in the registry, but I haven't confirmed how
  it's exposed

Ask and I'll dig into any of them properly.

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

# Glossary — the jargon, in plain language

Local AI is full of terminology. Nothing here is required reading — look things up as you hit them.

Ordered roughly from "you'll see this immediately" to "only if you go deep".

---

## Finding your way around

Everything lives in the **left sidebar**. Some entries are **groups** — click one to expand it, then
click the item inside. Written here as `Group → Item`.

| Sidebar entry | What's there |
|---|---|
| **Home** | Landing page |
| **Chat** | Talk to a model |
| **Knowledge Base** | Your own documents |
| **Development** | Development Mode ([read this first](privacy-and-data.md#development-mode-and-its-limits)) |
| **Models → Installed** | Models you already have |
| **Models → Recommendations** | **The advisor** — what fits your hardware |
| **Models → Loaded** | What's in memory now, and eject |
| **Automation → Agents** | Configurable assistants |
| **Automation → Skills** | Loadable agent capabilities |
| **Automation → MCP** | External tool servers |
| **Automation → Scheduler** | Unattended runs |
| **Automation → Tools** | Available tools |
| **Preview → Open Canvas** | Visual workflow builder (experimental) |
| **Preview → Image Generation** | Local image generation |
| **Settings → Node Settings** | Most settings, including voice |
| **Settings → Cloud Settings** | Optional external providers |
| **Settings → Diagnostics** | Export a bug report |
| **Invocations** / **Usage** | History and local token accounting |

> Looking for a **"Model Advisor"**? It is called **Models → Recommendations**.

---

## Installing and Windows words

These are the ones you'll hit **first**, before any AI terminology.

### ZIP / extract
A ZIP is a folder squashed into a single file. "Extracting" (or unzipping) unpacks it back into a real
folder. Windows can preview a ZIP as if it were a folder — **but the app won't run from that preview**;
you have to extract it properly first.

### Portable build
Software that doesn't install. You unzip a folder, run it from there, and delete the folder to remove
it. Nothing is written to Windows' installed-programs list. “Portable” describes the app's files, not its framework:
the Windows build still requires the separately installed x64 ASP.NET Core Runtime 10.0.10+.

### Mark of the Web / "Unblock"
Windows quietly tags files downloaded from the internet with a hidden "came from the internet" marker.
That marker is what makes Windows warn you later. **Unblocking** clears it.
→ [What that trade means](install-windows.md#step-2--unblock-the-zip-optional--but-read-this-first)

### Code signing / "unsigned"
Developers can pay for a certificate that cryptographically proves who published a program. This app
**is not signed**, so Windows can't verify who made it — which is why it warns you. Unsigned does not
mean unsafe, but it does mean *unverified*, and that's a real distinction.

### SmartScreen
Microsoft's warning system — the blue **"Windows protected your PC"** box. It flags programs it hasn't
seen before from a known publisher. → [What to click](install-windows.md#the-windows-smartscreen-warning)

### Checksum / SHA-256
A long fingerprint calculated from a file's exact contents. If your download's fingerprint matches the
published one, your copy is identical — nothing was corrupted or altered in transit.
→ [How to check](download-from-github.md#step-4--verify-sha-256)

### Console window
The black window full of scrolling text that opens alongside the app. It is **not** an error — it's the
app reporting what it's doing, and it's where problems show up. **Closing it stops the app.**

### PowerShell
A Windows command tool. A few optional steps here use it.

**To open it:** press the **Windows key**, type `powershell`, press **Enter**. Paste the command in and
press Enter.

### Node
The app's word for **your installation on this computer** — as in "your email never leaves this node",
or the files `node.sqlite` and `node.key`. It just means *this copy of the app, on this machine*.

### %LOCALAPPDATA%
A shortcut Windows understands for your personal app-data folder. Paste it into File Explorer's address
bar and it expands to the real path.

**To open File Explorer:** press **Windows key** + **E**. Click the wide bar across the top showing your
current folder, select whatever's in it, paste, and press Enter.

---

## The words you'll meet first

### Model
The AI itself — a large file containing everything it learned during training. The app runs models on
your computer instead of calling an online service. Different models have different strengths, sizes
and speeds.

### Parameters (0.5B, 7B, 14B, 70B)
Roughly, how big the model's "brain" is. **B** = billion.

- **0.5B** — tiny. Fast, runs anywhere, not very capable. *(This is the starter model.)*
- **7B–8B** — the sweet spot for most consumer machines. Genuinely useful.
- **14B–32B** — noticeably better, needs a good graphics card.
- **70B+** — excellent, needs serious hardware.

**Bigger is smarter but slower and needs more memory.** **Models → Recommendations** picks a size that fits you.

### Quantization (Q4_K_M, Q5_K_M, Q8_0…)
Compression for models. Full-size models are huge, so they get squeezed down to run on normal
computers.

- **Q6, Q8** — near-lossless. Rarely worth the extra memory.
- **Q5, Q4** — the working range. **`Q4_K_M` is where most people land.**
- **Q3** — a real quality drop, not a slight one. Use it to reach a model that otherwise won't fit.
- **Q2** — heavily degraded. Last resort, and only on large models.

You'll also see names like `IQ3_M` or `IQ4_XS` — newer "I-quants" that fit more quality into the same
size, at some extra CPU cost. Treat `IQ4_XS` as roughly `Q4_K_M`-class.

The app labels every option with its quality tier and tells you which ones fit your machine, so you
don't have to work this out yourself.

**Practical rule, and where it stops:** stepping *up* a model size and *down* to Q4 usually wins — a
14B at `Q4_K_M` beats an 8B at `Q8_0`. **That trade stops paying below Q4.** A 14B at `Q2_K` is
generally worse than an 8B at `Q4_K_M`, so don't keep trading quality downward to chase parameter
count.

### GGUF
The file format the models come in. If you see a `.gguf` file, that's a model this app can run. Not
something you need to think about — just the file extension.

### Token
How AI reads and writes text — roughly ¾ of a word each. "Tokens per second" is the speed measurement
you'll see while a reply streams in.

- **Under 5 tok/s** — slower than you read. Painful for long answers.
- **5–10 tok/s** — roughly reading speed. Usable.
- **15–30 tok/s** — comfortably ahead of you. This is the target.
- **50+ tok/s** — faster than you can follow.

### Time to first token
Before the first word appears, the model has to *read* your whole prompt — the conversation so far, the
agent's instructions, and any document excerpts pulled in for a knowledge-base question. That pass is
separate from the speed above.

**On a CPU with a lot of document context, this can take tens of seconds during which nothing appears
to happen.** If the app seems to hang before answering but streams normally once it starts, this is
why — not a crash.

### Context (context window)
The model's short-term memory — how much of the conversation it can see at once, measured in tokens.
When a conversation gets too long, the oldest parts fall out and the model "forgets" them.

Bigger context uses more memory. The app shows a context-usage indicator during chat.

### Prompt
Whatever you type. "System prompt" / "instructions" means the standing directions that shape how an
agent behaves in every conversation.

---

## Hardware words

### VRAM
The dedicated memory on your **graphics card** — the single biggest factor in what you can run. A model
must fit into VRAM to run at full speed.

Rough guide, assuming ~Q4 quants and a modest (8k) context:

| VRAM | Model size |
|---|---|
| 8 GB | 7B–8B |
| 12 GB | 12B–14B |
| 16 GB | 14B–24B |
| 24 GB | 27B–32B |
| 48 GB+ | 70B |

> **These numbers are the model weights only.** The conversation memory (context) also lives in VRAM
> and is **not** small — going from 8k to 32k can cost several GB on top. Your desktop and browser take
> another 0.5–1.5 GB.
>
> **If a model loaded fine last week and runs out of memory today, lower the context length first.**

### RAM
Your computer's normal memory. Used when there's no graphics card, or when a model is too big for VRAM
and gets split between the two. Slower than VRAM, but it works.

### CPU / GPU
- **CPU** — the main processor. Every computer has one. Runs models slowly but reliably.
- **GPU** — the graphics card. Far faster for AI, if you have a suitable one.

**You do not need a GPU.** The app works on CPU alone; answers just take longer.

### Offloading
Splitting a model between the graphics card and normal memory when it doesn't quite fit in VRAM. It
lets you run larger models than would otherwise be possible.

**Expect a large slowdown, not a small one.** Once part of the model sits in normal RAM, speed is
limited by much slower memory — often **5–20x** slower, not a gentle taper. A smaller model that fits
*entirely* in VRAM will usually feel far better than a bigger one that doesn't.

### CUDA / Vulkan
The two ways the app talks to your graphics card.

- **CUDA** — NVIDIA cards only. Fastest option.
- **Vulkan** — works with AMD, Intel and NVIDIA. The general-purpose path.

The app picks one automatically. You do not normally need to care.

---

## Feature words

### llama.cpp
The open-source engine that actually runs the models. The app downloads and manages it for you — you
never install it yourself.

### Hugging Face
A large public website hosting AI models, a bit like an app store for models. The app searches and
downloads from it directly.

### Agent
A configured assistant: its own instructions, personality, chosen model, and the tools it's allowed to
use. Think "a specialist" rather than "a general chatbot".

### Sub-agent
An agent that another agent can call for help with part of a task.

### Tool
Something an agent can *do* rather than just talk about — search your documents, read a file, call an
external service.

### Skill
A packaged set of instructions an agent can load on demand for a specific kind of job.

### Knowledge base
A collection of your own documents that the app indexes so agents can search and answer questions from
them.

### RAG (Retrieval-Augmented Generation)
The technique behind that: instead of hoping the model memorised something, the app **searches your
documents first**, then hands the relevant excerpts to the model to answer from. More accurate, and it
works with private files the model has never seen.

### Embedding
A numerical representation of meaning, used so search can find passages that *mean* the same thing even
when they share no words. Created locally by a small dedicated model.

### Reranking
A second, more careful pass over search results to reorder them by genuine relevance before the model
sees them.

### BM25 / FTS5
Classic keyword search — matching the actual words. The app combines this with meaning-based (vector)
search, because each catches what the other misses.

### MCP
**Model Context Protocol** — an open standard for connecting external tool servers to AI applications.
If you don't already know you want it, you don't need it.

### TTS (Text-to-Speech)
Reading replies aloud through voices exposed by your browser and operating system. Which voices are
available, and whether synthesis stays offline, depends on that platform's speech implementation and
is outside the app's control.

### WebGPU
A browser feature that lets web pages use your graphics card. The app's text-to-speech output uses
the browser's Web Speech implementation instead and does not require WebGPU.

---

## Words you'll only meet if you go deep

### Inference
Running a model to get an answer. "Inference speed" = how fast replies come out.

### Inference profile
Settings for how a model is launched on *your specific machine*, worked out once and then reused so you
get consistent performance.

### Temperature / top_p / min_p
Dials controlling randomness. **Low temperature** = focused, repetitive, predictable. **High** =
creative, varied, more likely to go off the rails. Defaults are sensible; ignore unless experimenting.

### Reasoning / thinking models
Models that work through a problem step by step before answering. Slower, but much better at logic and
maths. The app can show or hide that thinking.

### Context quantization (KV cache)
Compressing the conversation memory so longer chats fit in less VRAM.

### Eject
Unloading a model from memory to free VRAM, without closing the app.

### Ollama
A separate, popular local-AI program. If you already use it, this app can borrow its models. It will
not install or manage Ollama for you, and you do not need it.

### Development Mode
An experimental feature where an agent works on a real code repository of yours in an isolated copy,
with a review step before anything is written back. **Read
[its limits](privacy-and-data.md#development-mode-and-its-limits) before using it.**

### Loopback / 127.0.0.1
The address meaning "this computer, and only this computer". The app serves its interface there, which
is why it opens in a browser but is not reachable from your network or the internet.

---

Still met a word that isn't here? [Tell me](https://github.com/w0rldx/XE-Local-AI-Engine.Source/issues/new/choose) — a missing entry is a
documentation bug and I'd like to fix it.

---

**[← Back to the main page](../README.md)**

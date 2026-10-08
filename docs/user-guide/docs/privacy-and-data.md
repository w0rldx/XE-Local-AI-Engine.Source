# Privacy, your data, and the honest limits

The point of this application is that your conversations, documents and models stay on your computer.
This page states exactly what that does and does not guarantee — including where it falls short.

---

## The short version

| | |
|---|---|
| **Conversations sent to a cloud service** | No |
| **Documents uploaded anywhere** | No |
| **Analytics or telemetry** | None — there is no such service |
| **Online account required** | No |
| **Reachable from your network** | No — local only, enforced at startup |
| **Whole-database encryption** | **No** — important fields only, see below |
| **Internet needed at all** | Only for downloading models, engine components and updates |

---

## Where your data lives

Everything is in one folder:

```
%LOCALAPPDATA%\XE-Local-AI-Engine
```

Paste that into the File Explorer address bar to open it.

| Inside | What it is |
|---|---|
| `node.sqlite` | Your account, chats, agents, settings |
| `node.key` | The encryption key for the sensitive fields, locked with your admin password and recovery code |
| `models/` | Downloaded AI models (usually the bulk of the size) |
| `llama.cpp/`, `stable-diffusion.cpp/`, `whisper.cpp/` | The downloaded engines |
| `logs/` | Log files |

This folder is **separate from the app folder**, which is why updating never touches your data and
deleting the app folder alone doesn't remove it.

> **Stop the app before touching anything in here**, or you risk corrupting the database.

---

## What is encrypted, and what is not

**Be precise about this** — the distinction matters if your documents are sensitive.

### Encrypted (AES-256-GCM, with a key unique to your installation)

- Chat messages, conversation titles, message metadata
- Agent instructions and skills
- Tool arguments and results
- Workflow diagrams, and the inputs, outputs and errors of every workflow run
- Transcription session titles and settings, and every line of a transcript
- Uploaded file contents
- Locally generated images

> **Audio is never stored at all.** A recording you upload for transcription lives in one temporary
> file for the length of the transcription and is deleted afterwards; live microphone or shared-screen
> audio is never written to disk on either side. Nothing is sent anywhere — the model runs here. The
> transcript is the only thing kept, so deleting a transcription session deletes the only copy.

### Not encrypted — everything the knowledge base derives from your documents

❌ When you add a document to a knowledge base, the app extracts it and keeps the results **in the
clear**:

- the **extracted text**
- the **full-text search index** built from it
- the document's **headings and section structure**
- the numeric **"embeddings"** built from each passage
- the file's **type, size and content hash** (the *original file name* is encrypted)

**Embeddings are not a safe summary.** Enough of the original wording can be reconstructed from them
that you should treat them as being the text itself.

**Why:** local search has to read this to work. Encrypting it would break the search feature this app
exists to provide.

**The rule of thumb:** assume that **anything the search feature can find, the disk stores readably.**

### What that means for you

Anyone with access to your Windows user account — or to the disk, if it isn't encrypted — can read the
extracted text of documents you added to a knowledge base.

**If you work with genuinely sensitive documents, turn on full-disk encryption** (BitLocker on Windows).
That closes the gap properly, and is good practice regardless.

> **`node.key` warning:** never delete it on its own. It decrypts the sensitive fields in
> `node.sqlite`; removing it without the database leaves your chats permanently unreadable. Deleting
> both together is a clean reset and is fine.

> ### Your password locks the key
>
> `node.key` does not hold the key in readable form. On Windows and Linux alike it is locked with your
> **admin password**, and separately with the one-time **recovery code** shown at setup. A stolen disk
> or a copied data folder therefore does not reveal your chats without one of them. That is also why
> the app asks for your password every time it starts.
>
> **Lose both the password and the recovery code, and the data cannot be recovered** — by anyone.
>
> This protects the key while the app is stopped. While the app is running and unlocked, another
> program running as your user can still read the app's memory.

> ### ⚠️ On Windows, a backup only works under this Windows account
>
> Windows also ties part of the app's key store (the part that protects sign-in tokens for connected
> services) to your **Windows user account**. A copy of the data folder therefore **will not open**:
>
> - under a different Windows account
> - on a different PC
> - after reinstalling Windows or resetting your user profile
>
> In those cases the app **refuses to start** rather than pretending. Treat a data-folder backup as a
> *same-machine, same-account rollback only*. If you need conversations you can carry elsewhere, copy
> the text out instead.

> ### On Linux, a data-folder copy does move
>
> On Linux the same folder opens on another machine or under another account, once you enter the admin
> password or the recovery code. The files are also restricted to your user only (`0600`).
>
> Extracted knowledge-base text is still stored unencrypted on both systems. **If the data matters,
> turn on full-disk encryption.**

---

## What connects to the internet

### Automatically

| Where | Why |
|---|---|
| **Hugging Face** | Discovering and downloading AI models |
| **GitHub** | Downloading engine components, checking for app updates |

Those are the app-managed connections on a fresh installation. **No conversation, document or usage
data is transmitted to them.** Voice output uses the browser/operating-system Web Speech service;
available voices and whether a selected system voice uses the network are controlled by that
platform implementation, not this repository.

The complete list is the [egress table](../../wiki/12-security-and-privacy.md#1-egress-invariant-the-node-has-no-control-plane-channel):
model downloads, cloud chat providers, OpenAI-compatible providers, outbound MCP servers, update checks, and web
search and page fetches.

### Only if you switch it on

- **Web search and page fetching** — off by default. When you turn it on, searches go to DuckDuckGo, or
  to your own SearXNG server, and the model can download public web pages. The app asks you before each
  request and shows you the result before the model reads it, unless a conversation has
  **Auto-accept web content** on.
- **Cloud model providers** (Azure AI Foundry, Codex) — off unless you configure them. If you enable
  one, prompts you send to *that provider* go to *that provider*, exactly as you'd expect. Beyond your
  prompts, a cloud model gets nothing from your computer until you allow it. **Node Settings → Privacy →
  Cloud models** has five switches, all off by default, that apply from the next message or run:
  - **Let cloud models read local data** — your knowledge base, workspace files, attachments, earlier
    tool results and learned playbook memory.
  - **Let cloud models run unattended** — scheduled agents, graph workflow model steps, and integration
    triggers. Data an outside caller sends with a trigger then goes to the cloud provider too.
  - **Let cloud models use web tools** — web search, page fetching and HTTP custom tools, and only while
    Web access is on as well. The usual consent and approval prompts still apply.
  - **Let cloud models use MCP tools** — the tools of the MCP servers you registered.
  - **Let cloud models delegate to sub-agents** — handing part of a task to another agent, which then
    follows its own model's rules.

  No switch lets a cloud model run Python, AgentHome commands or command custom tools on your
  computer, and none changes Development Mode's own rules.
- **MCP servers you register.** An MCP server is a **separate program**, usually written by someone
  else, that the app launches. It runs **as you, with your permissions** — the same boundary as
  Development Mode, and with the same consequence: registering one is trusting its author with your
  machine, not just with a network connection. Only register servers you'd be willing to install and
  run yourself.
- **Custom tools you enable.** An HTTP custom tool sends its rendered request to the hosts you allow;
  a command custom tool launches a direct executable as your user. The node feature starts disabled,
  the built-in form initializes new definitions as disabled, and every call remains approval-wrapped.
  A fixed tool can reuse an explicit session approval until an edit changes its version; a parameterized
  tool asks again for every model-selected argument set. An API caller can request enablement for an
  acknowledged definition, and approval is not containment. Only enable definitions whose complete
  behavior and destination you trust.
- **The local model proxy** — this one goes the *other* way: it lets another program on **this machine**
  use your local models over an OpenAI-compatible API. Nothing leaves the computer — the proxy is
  loopback-only, like the rest of the app, and it additionally requires a bearer key you generate
  yourself in **Settings → Node Settings**. It is unusable until you generate that key, and revoking it
  closes the surface again.
- **OpenTelemetry export** — only if you deliberately configure an endpoint.
- **A dormant connection to an older platform this project grew from** — disabled on a fresh install
  and only active if explicitly enabled.

### No telemetry, at all

There is no analytics service and nothing to opt out of. I find out how the app behaves because you
tell me.

---

## Your local profile is not an account

Setup asks for an **email address and password**. This creates a login **on your own computer**.

- Nothing is sent anywhere; no server is contacted
- The email is never verified or used to contact you
- A made-up address is fine
- **There is no "forgot password" email** (no server to send one), but you can set a new password
  locally without losing data — [reset instructions](faq.md#i-forgot-my-password)

**Update checks do not require GitHub sign-in.** Official builds read the public release feed anonymously. No GitHub
device login or update token is stored.

---

## Local-only, and enforced

The app serves its interface on `127.0.0.1` — an address meaning "this computer only". It is not
reachable from your home network or the internet.

This isn't just a default: **after startup the app checks the addresses it actually bound to, and shuts
itself down if it finds a network-reachable one** — unless an operator has deliberately overridden the
guard. A misconfiguration that would expose it stops the app instead.

---

## Development Mode and its limits

> ### Enabled by default, with an off switch
>
> Development Mode ships enabled, and its pages are available on a stock install. To remove it, switch
> off **Development mode** under **Settings → Node Settings → General → Features** and restart the app.
> An operator can do the same before launch with `Development:Enabled=false`.
>
> What it **cannot** do is act on its own. It only touches a Git repository once **you register that
> repository and start a run**. Registering a repository is the real decision point.
>
> **Read this section before you do that.**

Development Mode lets an agent work on a real Git repository of yours. It works in an isolated copy
(a detached worktree), runs validation, and requires an explicit reviewed approval step before changes
reach your actual source.

### What it is not

> **It is not an operating-system security sandbox.**

Builds, tests, source generators and scripts **execute as your user account**, with your filesystem and
network access. If a repository contains a malicious build script, running it here is equivalent to
running it yourself.

The built-in protections are **application-level**: path restrictions, byte limits, environment
scrubbing, timeouts, process lifecycle handling, and hash-bound apply operations. Real, but not
containment.

**On Windows there is currently no OS-level containment underneath those controls.**

On Linux several optional best-effort mechanisms exist. Neither platform denies network access, because
operations like package restore legitimately need it.

A stronger, container-based execution provider is available as an **advanced opt-in** through
`Development:Sandbox:Provider=docker`. It requires a correctly configured Docker daemon and fails
closed rather than falling back to the process provider. The shipped configuration leaves it off;
on Linux, Docker socket access is also effectively root-equivalent, so it is not a free win. Unless
an operator has deliberately enabled and verified that provider, **assume the application-level
protections above are all you have.**

### The right mental model

> **Development Mode is an agent operating with your permissions, on a repository you chose.**

**Do not point it at code you do not trust.**

---

## Things to be aware of

- **Keep backups.** This is beta software. Don't let it be the only place important data lives.
- **The database only moves forward.** It upgrades when you update. Going back to an older build isn't
  guaranteed to work — back up the data folder first.
- **The build is unsigned because no signing certificate exists yet.** Windows and Linux security tools may warn you;
  verify `CHECKSUMS.sha256` before running a release. Signing is planned.
  [Why](faq.md#why-does-this-happen-at-all)
- **Logs may contain fragments of your activity.** They stay on your machine. The in-app support export
  (Diagnostics page) writes a zip to your disk with system information, the tail of the logs and the output
  of the model processes, scrubbed of home and data paths, e-mail addresses and token-shaped values. The app
  never uploads it. Names that appear outside a path and text a model process printed are not scrubbed, so
  skim the zip before you attach it to a public issue; a raw log file is not scrubbed at all.

---

## Questions

If something here is unclear or you think it's wrong, please
[ask](https://github.com/w0rldx/XE-Local-AI-Engine.Source/issues/new/choose). I would much rather answer a privacy question twice than have
someone assume something incorrect about their own data.

---

**[← Back to the main page](../README.md)**

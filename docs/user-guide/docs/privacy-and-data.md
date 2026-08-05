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
| `node.key` | The encryption key for the sensitive fields |
| `models/` | Downloaded AI models (usually the bulk of the size) |
| `llama.cpp/`, `stable-diffusion.cpp/` | The downloaded engines |
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
- Canvas graphs
- Uploaded file contents
- Locally generated images

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

> ### ⚠️ Your backup only works on this machine, under this Windows account
>
> On Windows, `node.key` is itself protected with **DPAPI, tied to your Windows user account**. That is
> a real security benefit — it is why someone who walks off with your hard drive still cannot read your
> chats.
>
> But it also means a copy of the data folder **will not open**:
>
> - under a different Windows account
> - on a different PC
> - after reinstalling Windows or resetting your user profile
>
> In those cases the app **refuses to start** rather than pretending. Treat a data-folder backup as a
> *same-machine, same-account rollback only*. If you need conversations you can carry elsewhere, copy
> the text out instead.

---

## What connects to the internet

### Automatically

| Where | Why |
|---|---|
| **Hugging Face** | Downloading models and voice files |
| **GitHub** | Downloading engine components, checking for app updates |

That's the complete list on a fresh installation. **No conversation, document or usage data is
transmitted to anything.**

### Only if you switch it on

- **Cloud model providers** (Azure AI Foundry, Codex) — off unless you configure them. If you enable
  one, prompts you send to *that provider* go to *that provider*, exactly as you'd expect.
- **MCP servers you register.** An MCP server is a **separate program**, usually written by someone
  else, that the app launches. It runs **as you, with your permissions** — the same boundary as
  Development Mode, and with the same consequence: registering one is trusting its author with your
  machine, not just with a network connection. Only register servers you'd be willing to install and
  run yourself.
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
- **There is no password recovery**, because there's nothing to recover it from —
  [reset instructions](faq.md#i-forgot-my-password)

**The GitHub sign-in is a separate thing.** It exists only so the in-app updater is authorised to check
GitHub for new releases. The token is stored locally. One is your app login; the other is update
permission.

---

## Local-only, and enforced

The app serves its interface on `127.0.0.1` — an address meaning "this computer only". It is not
reachable from your home network or the internet.

This isn't just a default: **after startup the app checks the addresses it actually bound to, and shuts
itself down if it finds a network-reachable one** — unless an operator has deliberately overridden the
guard. A misconfiguration that would expose it stops the app instead.

---

## Development Mode and its limits

> ### There is no switch to leave off
>
> Development Mode is **active as soon as the app starts** — it ships enabled, and its pages are
> available on a stock install. If you assumed you were protected by simply not turning it on, you
> were not.
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

A stronger, container-based execution mode exists in the code, but **it is not available to you in this
build** — it needs hand-edited configuration and a correctly set-up Docker daemon, and that's not
something I'm asking users to do. (On Linux, Docker socket access is also effectively root-equivalent,
so it is not a free win.) **Assume the application-level protections above are all you have.**

### The right mental model

> **Development Mode is an agent operating with your permissions, on a repository you chose.**

**Do not point it at code you do not trust.**

---

## Things to be aware of

- **Keep backups.** This is beta software. Don't let it be the only place important data lives.
- **The database only moves forward.** It upgrades when you update. Going back to an older build isn't
  guaranteed to work — back up the data folder first.
- **The build is unsigned.** Windows will warn you.
  [Why](faq.md#why-does-this-happen-at-all)
- **Logs may contain fragments of your activity.** They stay on your machine. If you send me a log for
  a bug report, skim it first — the in-app diagnostics export redacts secrets, but a raw log is raw.

---

## Questions

If something here is unclear or you think it's wrong, please
[ask](https://github.com/w0rldx/XE-Local-AI-Engine.Source/issues/new/choose). I would much rather answer a privacy question twice than have
someone assume something incorrect about their own data.

---

**[← Back to the main page](../README.md)**

# FAQ & troubleshooting

Common problems and questions. If your issue isn't here,
[open an issue](https://github.com/w0rldx/XE-Local-AI-Engine.Source/issues/new/choose) — and please tell me, because a missing entry is worth
fixing.

**Jump to:** [Installing](#installing--starting) · [Windows warnings](#windows-security-warnings) ·
[Models](#models--performance) · [Using it](#using-the-app) · [Data & privacy](#data-privacy--reset) ·
[Updating](#updating) · [General questions](#general-questions)

---

## Installing & starting

### The releases page shows nothing / I get a 404
This repository is public, so you don't need to sign in or be invited to see it. A 404 here usually
means a bad or outdated link — double-check you're using [the Releases page](https://github.com/w0rldx/XE-Local-AI-Engine.Source/releases/latest)
directly. If it's still not loading, it may be a temporary GitHub outage; try again shortly, or
[open an issue](https://github.com/w0rldx/XE-Local-AI-Engine.Source/issues/new/choose).

### I downloaded it but there's no `.exe` inside
You almost certainly clicked the green **`<> Code`** button, which downloads this repository's
**source code** rather than the packaged app.

Go back to the [Releases page](https://github.com/w0rldx/XE-Local-AI-Engine.Source/releases/latest), expand **Assets**, and take
`XE-Local-AI-Engine-win-Portable.zip`. → [Full walkthrough](download-from-github.md)

### Which file do I actually run?
**`XE-Local-AI-Engine.exe`** in the **top-level** folder — the one sitting *next to* the `current`
folder.

**Not** `current\XE-Local-AI-Engine.Client.exe`. That second file is much larger, so people assume it's
the real one. **It will not start properly** — launching it directly skips the desktop setup entirely,
so it won't find your data folder and won't open your browser.

### Nothing happens when I run it — the window flashes and disappears
- **Is it already running?** Only one copy can run at a time. A second launch prints *"Another
  instance ... is already running"* and closes immediately. Check your taskbar for an existing console
  window. Remember that **closing the browser tab does not stop the app**.
- Wait a minute — the first launch is slow and quiet.
- Check whether a **console window** opened. It carries the real error messages.
- Make sure you extracted the ZIP properly. Running the `.exe` from *inside* the ZIP preview window
  cannot work — Windows shows ZIP contents as if they were a folder, but the app needs its files on
  disk.
- Try extracting to a simpler path such as `C:\Apps\XE-Local-AI-Engine`.

### The first-time setup seems stuck — it's been 20 minutes
First launch downloads the AI engine plus a ~400 MB starter model, so **5–15 minutes is normal**, and
longer on a slow connection.

**Check the console window**, not the browser. If lines are still appearing or numbers still changing,
it's working — leave it alone. The finish line is a line containing **`Now listening on:`**.

If the *same last line* has been there for **10+ minutes**, it's genuinely stuck:

1. Close the console window.
2. Delete `%LOCALAPPDATA%\XE-Local-AI-Engine` (paste that into the File Explorer address bar).
3. Start the app again.

If it stalls at the same point twice, **please report it** with the last few console lines — that's a
real bug. → [How to report](feedback.md)

### The console opens but the browser doesn't
Look in the console for a line containing `http://127.0.0.1:` followed by a number, and paste that
address into your browser.

### It says the port is in use / won't connect
Only run **one copy at a time**. If you're sure none is running:

1. Close all console windows.
2. Delete `desktop-port.txt` from your data folder (paste `%LOCALAPPDATA%\XE-Local-AI-Engine` into
   File Explorer).
3. Start the app again — it picks a fresh port.

### Can I put it on a USB stick or network drive?
It will work but feel broken — models are gigabytes and get read constantly. Use a local disk.

### Can I move the folder after installing?
Yes. Your data lives elsewhere (`%LOCALAPPDATA%\XE-Local-AI-Engine`), so chats and models survive the
move. Windows may show its security warning once more afterwards.

---

## Windows security warnings

### "Windows protected your PC" — I only see "Don't run"
**Click "More info" first.** It's a small text link, not a button, and the **"Run anyway"** button
doesn't appear until you click it.

This catches almost everyone. → [Step-by-step with screenshots](install-windows.md#the-windows-smartscreen-warning)

### Why does this happen at all?
The build is **not code-signed**. A signing certificate costs several hundred euros a year and I
haven't bought one yet. Windows warns about any unfamiliar unsigned program — it is a
statement about the certificate, not about the file being harmful.

### How do I avoid the warning entirely?
Unblock the ZIP **before** extracting: right-click it → **Properties** → tick **Unblock** → **OK**.

That clears the "downloaded from the internet" marker once, instead of letting it spread to every
extracted file. → [Details](install-windows.md#step-2--unblock-the-zip-optional--but-read-this-first)

### There's no "More info" link at all
Some managed machines hide it. Try the Unblock step above, or:

```powershell
Unblock-File "C:\Apps\XE-Local-AI-Engine\XE-Local-AI-Engine.exe"
```

If your computer is managed by an employer or school, SmartScreen may be enforced by policy and cannot
be bypassed. Please use a personal machine rather than fighting your IT department over a beta.

### My antivirus deleted it
Unsigned, brand-new, single-large-executable programs are a shape some scanners distrust by default.

You can add the folder to your antivirus exclusions **if you're comfortable doing that** — and it's
entirely reasonable to decide you'd rather wait for a signed build. Verify the download first if you
like: GitHub publishes a SHA-256 digest beside each release asset.
→ [How to check](download-from-github.md#step-6--check-you-got-the-right-thing)

### Windows Firewall is asking about a port I don't recognise

Expected, and safe to allow — or to deny.

The app itself runs on `127.0.0.1`. The **model engines run as separate local programs** on their own
loopback ports, and Windows may prompt about those the first time you chat or generate an image:

| Program | Ports |
|---|---|
| llama.cpp (chat/text) | `18100`–`18199` |
| stable-diffusion.cpp (images) | `18200`–`18299` |

These are **local-only** connections between parts of the app on your own machine — nothing is being
opened to the internet or your network.

> **If you're offered "Private networks" and "Public networks", you can safely untick both.** The app
> only needs loopback, which the firewall doesn't gate. If something then fails to start, tell me.

### Is it safe?
Straight answer: **you are trusting an unsigned build from one developer, and you can check the source
yourself.** The build is unsigned, so Windows cannot tell you who made it. What you do have is the
public source code in this repository, GitHub's SHA-256 digest confirming your download wasn't
tampered with in transit, and the option to build it yourself instead of trusting the binary.

It's also worth being clear about what running it means, because this is true of **any** desktop app
you install: it runs **as you**, with access to your files and your network.

The local-only design (`127.0.0.1`, plus a startup check that shuts the app down if it ever binds a
network-reachable address) stops *other people* reaching it. It does **not** limit what the program
itself can do on your machine. Those are two different questions and I don't want to answer the second
one with the first. → [Full detail](privacy-and-data.md)

**If that's not enough, don't run it.** Waiting for a signed release is a completely sensible decision,
and I would much rather you made it than felt pushed into anything.

---

## Models & performance

### Replies are extremely slow
Most likely you're running on the **CPU** rather than the graphics card.

- **NVIDIA:** install current drivers — the app detects the card through them. No drivers → CPU mode.
- **AMD / Intel:** supported through Vulkan and *should* be accelerated. If yours seems to be running
  on CPU, **please report it** — see below.
- **No GPU:** normal and expected. Use a smaller model (3B or below) and be patient.

> **Note for AMD and Intel users on Windows:** builds before `v0.1.0-rc.5.0` had a bug that silently
> ran these cards on the CPU. **It is fixed from rc.5.0 onward.** If you're on an older build, update.
>
> **Two known gaps remain on AMD/Intel Windows machines:**
>
> 1. The app **cannot yet read how much VRAM those cards have.** Inference still runs on the GPU, but
>    **Models → Recommendations sizes its advice from system RAM instead**, so it is less precise. If a
>    recommended model fails to load, drop one size or one quantization level.
> 2. The app **cannot yet reliably warn you** if it does end up on the CPU.
>
> So if performance seems far worse than expected, please tell me your GPU — that's a useful report.

### How do I tell whether it's using my GPU?
Check the console window during startup and model loading — it reports the runtime and the devices it
found. The hardware card in the app also shows the detected GPU vendor.

If the console shows no GPU devices when you have one, that's worth reporting.

### The starter model gives terrible answers
That's expected — it's a 0.5B model chosen for download size, not quality. **Replace it.** The Model
Advisor recommends something appropriate for your hardware, and the difference is dramatic.
→ [How to swap it](first-run.md#step-4--get-a-model-that-is-actually-good)

### A model won't load / out of memory
It's too big for your VRAM or RAM.

- **Lower the context length first.** Context lives in VRAM alongside the model and is usually what
  pushed you over — especially if the same model loaded fine before.
- Pick a **smaller quantization** of the same model (`Q4_K_M` before `Q8_0`) — usually better than
  dropping to a smaller model. Don't go below Q4 unless you have to.
- **Eject** models under **Models → Loaded** you're not using (**Models → Loaded**) to free memory.
- Don't keep an image model and a large chat model loaded simultaneously.
- Close other GPU-heavy applications, including games and browsers with many tabs.

### Which model should I choose?
Use **Models → Recommendations** in the left sidebar and take the **★ Recommended** pick. It measures your actual hardware. If you
want to understand the numbers, see the [Glossary](glossary.md#parameters-05b-7b-14b-70b).

### A download failed or got stuck
Retry it — Hugging Face occasionally rate-limits. Check free disk space; models are large. If it fails
repeatedly, please report it with the model name.

### Can I use GGUF models I already have?
**Not in this build — there's no import path.** No import button, no scan-a-folder setting. Dropping
`.gguf` files into the models directory is *not* reliably picked up, because the model list is driven
by a manifest rather than by scanning the folder.

If you have an existing model library this is a real limitation, and **worth telling me about** — it's
the most likely reason someone with a big collection gives up early.

What *does* work: if you already run **Ollama**, the app can list and use its models. It won't install
or manage Ollama for you.

→ [Technical detail](for-experienced-users.md#using-ggufs-you-already-have--not-supported)

---

## Using the app

### How do I stop it?
**Close the console window.** That stops the app and the AI engine together, cleanly. Closing the
browser tab only hides the interface — the app keeps running.

### Can I use it offline?
Yes, once the engine and a model are downloaded. Internet is only needed for downloading models,
runtimes and updates.

### It answers about my documents incorrectly, or can't find them
- Confirm the document finished processing (it's indexed after upload, not instantly).
- Very large or scanned/image-only PDFs may extract poorly — there is no OCR.
- Try more specific wording; search combines keyword and meaning-based matching.

### Can it hear me / is there voice input?
**No speech-to-text yet.** The app can read replies aloud, but it cannot listen. Not two-way voice chat.

### Is Development Mode safe to try?
It runs commands (builds, tests, scripts) **as you, with your permissions**. The protections are
application-level, not an operating-system sandbox. On Windows there is no OS-level containment
underneath.

**Only point it at code you trust.** → [The full boundary](privacy-and-data.md#development-mode-and-its-limits)

---

## Data, privacy & reset

### Where is my data stored?
One folder, separate from the app:

```
%LOCALAPPDATA%\XE-Local-AI-Engine
```

Paste that into the File Explorer address bar. It holds your account, chats, settings, downloaded
models and the AI engine — often several GB.

### Does anything get sent to the internet?
No conversations, no documents, no telemetry. The app connects out only to **Hugging Face** (models and
voices) and **GitHub** (engine components and updates) — plus any cloud provider you deliberately
configure. → [Full detail](privacy-and-data.md)

### Is my data encrypted?
**Partly, and the distinction matters.** Chats, agent instructions, uploaded files and generated images
are individually encrypted. **Extracted knowledge-base text and its search index are stored
unencrypted**, because local full-text search needs to read them.

If you work with genuinely sensitive documents, use full-disk encryption (BitLocker) as well.
→ [Details](privacy-and-data.md)

### How do I completely reset the app?
1. **Stop the app** (close the console window).
2. Delete the data folder: paste `%LOCALAPPDATA%\XE-Local-AI-Engine` into File Explorer and delete it.
3. Start the app again — it sets itself up from scratch.

This wipes your account, chats, settings **and downloaded models**. The app itself stays installed.

<details>
<summary><b>Reset your data but keep the downloaded models</b> (avoids re-downloading gigabytes)</summary>

With the app stopped, in PowerShell:

```powershell
$d = "$env:LOCALAPPDATA\XE-Local-AI-Engine"
Remove-Item -Force "$d\node.sqlite","$d\node.key"
Remove-Item -Force "$d\*.enc"
```

Deletes your account, chats and settings; keeps models and the engine.

</details>

> **Never delete `node.key` on its own.** It decrypts the sensitive fields in `node.sqlite` — removing
> it without the database makes your chats permanently unreadable. Deleting both together is fine.

### The "Create admin" button won't work during setup
The password rules are strict and the button stays disabled until **all** are satisfied:

- at least **12 characters** · an **uppercase** letter · a **lowercase** letter · a **digit** ·
  a **symbol** (e.g. `!@#$%`)

One unmet rule is almost always the reason. → [Setup walkthrough](first-run.md#step-2--create-your-local-profile)

### Do I need a real email address to sign up?
**No.** It's stored only on your machine and is never contacted, verified or transmitted — a made-up
address works. It isn't even your username: **signing in afterwards asks for the password only.**

### I forgot my password
There's no recovery, because there's no server to email you. Reset the app (above). You can keep your
downloaded models using the collapsed section.

### How do I uninstall it?
There's no uninstaller. Three steps:

1. **Stop the app first** — close the console window. Deleting files while it's running can corrupt
   the database.
2. Delete the folder you extracted.
3. Delete `%LOCALAPPDATA%\XE-Local-AI-Engine`.

Done — nothing is written to the registry or to Program Files.

---

## Updating

### How do I get new versions?
Two ways:

- **In-app:** the app checks for updates and can install them itself. This requires a one-time GitHub
  sign-in so the updater is authorised to check GitHub for new releases.
- **Manually:** download the new ZIP from Releases and extract it fresh. Your data folder is separate,
  so chats and models carry over.

→ [Updating guide](updating.md)

### Why does it want me to sign in to GitHub?
The in-app updater needs its own authorisation to check GitHub for new releases and download them. This
is **separate** from your local app profile — one is your login to the app, the other only lets the
updater check for releases. The token is stored on your machine.

### Can I go back to an older version?
Yes — download an older release and run it. **But back up your data folder first:** the database
upgrades automatically to newer versions and isn't guaranteed to work with older builds.

---

## General questions

### Is this open source? Can I see the code?
Yes. This repository **is** the source code, licensed under **Apache-2.0**. You're free to read it,
build it yourself, and redistribute it under that licence.

### Is it free? Will it stay free?
Free to use today. No pricing decisions have been made about the future.

### macOS? ARM? Linux?
**Windows and Linux, x64 only.** No macOS or ARM build exists. Linux is my main development
environment and is published here as a portable ZIP — the one difference is that it **does not update
itself**, so you replace it by hand → [Linux installation](install-linux.md).

### Do I need Docker / Ollama / Python / .NET?
**No.** Everything needed ships in the download. Ollama is optional and only if you already use it.

### How much disk space will this really use?
- App: ~100 MB to download, ~275 MB once extracted
- Engine + starter model: ~1 GB
- A useful model: **4–20 GB each**

**5 GB gets you the starter model and nothing else.** Budget **10 GB** to run one real model, and
**30 GB+** if you want to compare a few.

### Can I run it on a server / access it from another computer?
Not supported. The app binds to `127.0.0.1` and deliberately **shuts down** if it detects a
network-reachable address. It's a local desktop application by design.

### Will it work on my machine?
If it's 64-bit Windows 10/11 with 8 GB RAM and 5 GB free disk — yes, at least on CPU. A graphics card
makes it fast rather than possible.

**Modest hardware is especially useful to test on**, so please don't count yourself out.

---

## Reporting something not covered here

→ [**How to send feedback**](feedback.md), or [open an issue](https://github.com/w0rldx/XE-Local-AI-Engine.Source/issues/new/choose).

Useful to include: what you did, what happened, your Windows version, your CPU/GPU/RAM, and any red
text from the console window. **"I got confused here" is a valid and valuable bug report.**

---

**[← Back to the main page](../README.md)**

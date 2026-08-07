# Your first run

What happens the first time you start the app, and what to do in it.

**Expect the first launch to take 5–15 minutes.** Most of that is downloading, and it only happens
once.

---

## Step 1 — Wait for the first-time setup

When the app starts for the first time it downloads two things in the background:

1. **The AI engine** (called *llama.cpp*) — the component that actually runs models.
2. **A small starter model** — about 400 MB, downloaded from Hugging Face.

While this happens the app can look frozen or unhelpfully quiet. **It isn't stuck.** The black console
window shows the real progress — watch that if you want reassurance.

**You need an internet connection for this.** After the initial download, chatting works offline.

### How to tell it's working, not dead

The console is your progress bar. You're looking for **lines that keep changing** — download
percentages, model or runtime names, or messages about extracting and starting.

**The finish line is a line containing `Now listening on:` followed by a `http://127.0.0.1:...`
address.** Once you see that, setup is done and the browser page will move on to the **"Set up local
admin"** form.

| What you see | What it means |
|---|---|
| Lines still appearing, numbers changing | Working. Leave it alone. |
| `Now listening on: http://127.0.0.1:…` | **Done** — go to your browser. |
| The *same* last line for 10+ minutes | Probably stuck — see below. |

> **If it really is stuck** (nothing has changed for 10+ minutes): close the console, delete
> `%LOCALAPPDATA%\XE-Local-AI-Engine`, and start again. If it stalls at the same place twice,
> **that's a bug worth reporting** — please send me the last few console lines.
> → [How to report it](feedback.md)

> **Needs about 2 GB free disk space** for this stage. Real models you add later are much bigger — plan
> for 5–30 GB if you get into it.

---

## Step 2 — Create your local profile

You'll see **"Set up local admin"**, asking for an email address and a password.

> **"Admin" here does not mean Windows Administrator.** It means the owner account *inside this app* —
> the first user of your own installation. It has nothing to do with Windows administrator rights, and
> the app never asks for them.

![Creating the local admin account](../media/screenshots/setup-wizard@2x.png)

> ### This is not an online account
>
> You are **not** signing up for anything, and nothing is sent anywhere. This creates a login on your
> own computer, stored on your own disk. The screen says so too: *"Stored only on this device. Your
> email never leaves this node."*
>
> The email is just an identifier — never contacted, never verified, never transmitted. **A made-up
> address is fine.**

### The password rules are strict

This catches people out. Your password must have **all** of:

- At least **12 characters**
- An **uppercase** letter (A–Z)
- A **lowercase** letter (a–z)
- A **digit** (0–9)
- A **symbol** (e.g. `!@#$%`)

The **Create admin** button stays greyed out until every rule is met. If nothing seems to happen when
you click it, check the list — one unmet rule is usually why.

> ### ⚠️ Write this password down somewhere safe
>
> There is **no "forgot password" email**, because there is no server that could send one. If you do
> lose it, you can set a new one with a short one-line command **without losing your data**.
> → [I forgot my password](faq.md#i-forgot-my-password)

**After this, signing in asks for the password only** — not the email. The email is not your username;
it is just stored with the account.

<p align="center">
  <img src="../media/screenshots/login@2x.png" alt="Signing back in with the password only" width="800">
</p>

---

## Step 3 — Have your first chat

Once you're in, there's a guided tour you can follow, or you can go straight to **Chat** and type
something.

<p align="center">
  <img src="../media/screenshots/onboarding-tour@2x.png" alt="The optional guided tour on first sign-in" width="800">
</p>

The first model is already selected, so it should just work.

<p align="center">
  <img src="../media/screenshots/chat@2x.png" alt="The chat interface" width="800">
</p>

**If a reply appears, everything is working.** That is genuinely the milestone for the first session —
the rest can wait.

---

## Step 4 — Get a model that is actually good

> ### ⚠️ Please read this part — it prevents the most common bad first impression
>
> The starter model is **tiny — 0.5 billion parameters**. It exists purely to prove that chat works on
> your machine without a huge download.
>
> **It will feel dumb.** It forgets things, gets facts wrong, and rambles. That is the model, not the
> app, and it is **not** what this software is capable of.
>
> Swapping it for a real model takes about five minutes and transforms the experience. Please do this
> before forming an opinion.

### Let the app choose for you

Go to **Models → Recommendations** in the left sidebar. It measures your actual hardware — memory,
graphics card and VRAM — and recommends models that will genuinely run well on *your* machine.

> **AMD/Intel on Windows:** VRAM can't be read yet, so advice for those cards is based on system RAM
> and is less precise. If a recommended model won't load, step down one size or quantization level.

<p align="center">
  <img src="../media/screenshots/model-advisor@2x.png" alt="Model recommendations" width="800">
</p>

Look for the **★ Recommended** pick and download it. Larger models are more capable but slower and need
more memory; the advisor already accounts for that, so **trusting its recommendation is the right move**
if you're unsure.

<details>
<summary><b>What the numbers mean</b> (optional reading)</summary>

- **Parameters (0.5B, 7B, 14B…)** — roughly, model size. Bigger is usually smarter and always slower.
- **Quantization (Q4_K_M, Q8_0…)** — compression. Lower numbers = smaller and faster, but slightly less
  accurate. `Q4_K_M` is the usual sweet spot.
- **FITS / doesn't fit** — whether it can load into your memory at all.

All of these are explained properly in the [Glossary](glossary.md).

</details>

### Or browse yourself

On the **Models → Installed** page there's a Hugging Face browse panel — search that public library of
models from inside the app. Each download option is labelled with its size and whether it fits your
hardware.

<p align="center">
  <img src="../media/screenshots/model-download-quants@2x.png" alt="Quantization options with hardware fit" width="800">
</p>

---

## Step 5 — Explore, at your own pace

No need to do these in order, or at all.

Everything below is in the **left sidebar**. `Group → Item` means: click the group to expand it, then
click the item.

| Try this | Where in the sidebar | Good for |
|---|---|---|
| **Ask about your own documents** | **Knowledge Base** | Notes, PDFs, manuals |
| **Make a custom assistant** | **Automation → Agents** | An agent with your own instructions and persona |
| **Generate an image** | **Preview → Image Generation** | Local image generation |
| **Run an agent on a schedule** | **Automation → Scheduler** | Unattended, repeating tasks |
| **Give agents extra abilities** | **Automation → Skills** | Loadable capabilities |
| **Connect external tools** | **Automation → MCP** | Advanced — see the [Glossary](glossary.md#mcp) |
| **Try the visual workflow builder** | **Preview → Open Canvas** | Experimental |
| **See what's using memory** | **Models → Loaded** | Freeing up VRAM |
| **Change settings** | **Settings → Node Settings** | Voice, and everything else |
| **Export a bug report** | **Settings → Diagnostics** | [Reporting problems](feedback.md) |

> **Development Mode** lets an agent edit a real code repository of yours. It is powerful and
> **genuinely risky**.
>
> Note there is **no switch to leave off** — it ships enabled. What gates it is that it only touches a
> repository once *you register one and start a run*. Please read
> [its security boundary](privacy-and-data.md#development-mode-and-its-limits) **before you register a
> repository**, and never point it at code you do not trust.

---

## Stopping and starting again

- **To stop:** close the **console window** (not just the browser tab).
- **To start again:** run `XE-Local-AI-Engine.exe` from the same folder.

Your models, chats and settings are all still there — they live in a separate data folder, not in the
app folder.

---

## What good feedback looks like right now

If you only tell me one thing, make it one of these:

- **Where did you get stuck or confused?** Especially in the first 10 minutes.
- **What did you expect to happen that didn't?**
- **Was it fast enough to be useful on your hardware?** (Please mention your GPU/CPU and RAM.)
- **What would stop you using this for real work?**

→ [How to send feedback](feedback.md)

---

## Something went wrong?

→ [**FAQ & troubleshooting**](faq.md)

---

**[← Back to the main page](../README.md)**

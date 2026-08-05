# Giving feedback

**There is no required report and no obligation.** Anything you send is genuinely useful — including
"I opened it, didn't understand what to do, and gave up." That's not a failed test; that's a finding.

---

## Where to send it

| | |
|---|---|
| **Bug or problem** | [Open an issue](https://github.com/w0rldx/XE-Local-AI-Engine.Source/issues/new/choose) |
| **Impressions, ideas, questions** | [Open an issue](https://github.com/w0rldx/XE-Local-AI-Engine.Source/issues/new/choose) or message me on Reddit |
| **Something private** | Message me directly — don't post it in an issue |

Never post logs, screenshots or documents containing personal or confidential information. Issues are
visible to everyone with repository access.

---

## The most useful things you can tell me

You don't need to cover all of these. Any one is worth having.

### 1. Where you got stuck
Especially in the **first ten minutes**. Confusion in the first session is the highest-value feedback
there is, because I can no longer see this software with fresh eyes.

### 2. What you expected that didn't happen
Mismatches between expectation and behaviour are usually design problems, not user error.

### 3. How it performed on your hardware
Please mention your **CPU, graphics card and RAM**. "It was too slow to be useful on a GTX 1660" tells
me something I cannot find out any other way — I don't own your machine.

### 4. What would stop you using it for real
The honest blocker. Missing feature, trust concern, too slow, too confusing, already using something
better. **Blunt answers here are the most valuable ones**, and won't offend me.

### 5. Where it's heading in the wrong direction
If something seems overbuilt, pointless, or badly conceived — say so.

---

## Reporting a bug

### The best version: an in-app diagnostics snapshot

1. In the left sidebar, open **Settings → Diagnostics**.
2. Use **"Report a problem"** to export a snapshot.
3. Attach it to your issue.

It captures recent activity, network calls and errors, **with secrets redacted**.

### If you can't do that, include:

- **What you did** — the steps, as plainly as you can
- **What happened** vs **what you expected**
- **The app version** — e.g. `v0.1.0-rc.5.0`, from the release you downloaded
- **Your system** — Windows version, CPU, GPU, RAM
- **Any red text in the console window** — copy it as text if possible
- **A screenshot**, if it's a visual problem

### Console log lines

The black console window carries the real errors. To copy from it:

1. **Select the text with your mouse and press `Ctrl`+`C`.**
2. Paste it into your issue.

(On older Windows 10 consoles you may need to right-click → **Mark** first, then select and press
**Enter**.)

Or attach a log file from `%LOCALAPPDATA%\XE-Local-AI-Engine\logs`.

> **Skim a log before sending it.** The diagnostics export redacts secrets; a raw log file does not.

---

## What happens to your report

I read everything. I'm one person doing this in my spare time alongside a full-time job, so replies may
take a few days — but nothing gets ignored.

Feedback directly shapes what I work on next. Limited time means the choice of *what* to fix is the
most consequential decision I make, and user feedback is what informs it.

---

## What I'm not asking for

- **No public review or promotion.** Not expected, not wanted as a condition.
- **No minimum amount of testing.** Try it once and never open it again — that's fine, and *why* you
  didn't come back is itself the useful part.
- **No polished bug reports.** A messy description of a real problem beats a well-formatted
  non-problem.

---

Thank you. Honest impressions from people on hardware I don't own is the single most valuable input
this project can get right now.

---

**[← Back to the main page](../README.md)**

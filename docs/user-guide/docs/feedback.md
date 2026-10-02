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

### The best version: the in-app support export

1. Click the **bug button** in the header (the "Report a problem" action). This captures a snapshot and
   opens the **Diagnostics** page.
2. On the snapshot, click **Export**. You get one file, `xe-support-<id>.zip`, on your own disk.
3. Click **Open GitHub issue**. A new tab opens the bug form with your app version, operating system,
   browser and hardware already filled in. Nothing is sent by the app itself.
4. **Review the zip**, then drag it into the issue.

The zip holds the browser snapshot (recent activity, network calls and errors) plus system information
and the tail of the engine's logs, including the output of the model processes it started. Home and data
folders, e-mail addresses and token-shaped values are scrubbed automatically. That is a safety net, not a
guarantee: a bare user name or computer name, and text a model process printed (prompts, file names), can
remain. Open the zip and skim it before attaching it to a public issue.

**If the problem is reproducible**, capture more detail first:

1. On the **Diagnostics** page, turn on **Verbose logging until restart**.
2. Reproduce the problem.
3. Export the zip as above.

Verbose logging turns itself off the next time the engine restarts.

If the engine cannot be reached, the export still works but contains only the browser snapshot, and a
message says so.

### If you can't do that, include:

- **What you did** — the steps, as plainly as you can
- **What happened** vs **what you expected**
- **The app version** — e.g. `v0.1.0-rc.5.1`, from the release you downloaded
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

> **Skim a log before sending it.** The support export scrubs paths, e-mail addresses and tokens; a raw log file does not.

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

# Updating to a new build

New tester builds appear on the [Releases page](../../../releases/latest). There are two ways to get
them.

**Your data is never touched by an update.** Chats, models and settings live in a separate folder, so
they carry across every version.

> ### 🐧 On Linux, only Option B applies
>
> The Linux build ships with the in-app updater **switched off** — a portable ZIP is not something an
> updater can safely rewrite in place. There is no GitHub sign-in to do and no update button to press;
> you replace the folder by hand, which takes about a minute.
> **→ [Updating a Linux build](install-linux.md#updating-to-a-new-version)**
>
> Everything under Option A below is Windows-only.

---

## Option A — Update inside the app *(recommended, Windows)*

The app can update itself. It needs a **one-time GitHub sign-in** first, because the builds are in a
private repository.

### The one-time sign-in

The app uses GitHub's **device code** flow — you never type your GitHub password into the app.

1. In the app, go to the update or settings area and start the GitHub sign-in.
2. The app shows a **short code** (something like `ABCD-1234`).
3. It opens [github.com/login/device](https://github.com/login/device) in your browser — or open that
   address yourself.
4. **Type the code** into GitHub and confirm.
5. Return to the app. It's now authorised.

The resulting token is stored encrypted in your data folder. You should only need to do this once.

**What it can and cannot do.** This is a GitHub *App* authorisation, not a broad account grant. It is
configured for **read-only access to repository contents** on the release repository. It cannot write
to any repository, cannot open issues or push code, and cannot see repositories the App isn't
installed on.

> ### Signing out in the app does not revoke it at GitHub
>
> Signing out deletes the **local copy** of the token only — the authorisation itself stays on your
> GitHub account until you remove it there.
>
> To revoke it properly: **github.com → Settings → Applications → Authorized GitHub Apps** → revoke.
>
> You can do that any time; the app will simply ask you to sign in again if it needs to update.

> **Why is this needed?** The releases are private, so the updater needs permission to read them.
>
> **This is not related to your app login.** Your local profile is a login to the app on your computer;
> the GitHub authorisation only grants access to tester downloads. Two separate things.

### After that

The app checks for new versions and can download and install them itself. Restart when prompted.

<details>
<summary><b>The sign-in isn't offered, or updating does nothing</b></summary>

In-app updating only works for the **portable Velopack build downloaded from this repository's
Releases page**. If you're running a build from somewhere else, updating is intentionally disabled and
you should use Option B.

Also check: you're signed in to GitHub with the account I invited, and you accepted the invitation.

</details>

---

## Option B — Update manually

Always works, and it is the **only** way on Linux.

### Windows

1. **Stop the app** — close the console window.
2. Download the new `XE-Local-AI-Engine-win-Portable.zip` from
   [Releases](../../../releases/latest).
   → [Download walkthrough](download-from-github.md)
3. **Right-click the ZIP → Properties → Unblock → OK** (avoids the Windows warning again).
4. Extract it — either over the old folder, or to a new one and delete the old afterwards.
5. Run `XE-Local-AI-Engine.exe` from the top-level folder.

### Linux

1. **Stop the app** — close the terminal, or press `Ctrl+C` in it.
2. Download the new `XE-Local-AI-Engine-<version>-linux-Portable.zip` from
   [Releases](../../../releases/latest).
3. Unzip it. It expands into its own **versioned folder**, so it lands beside the old one rather than
   over it.
4. Run `./start-xe-local-ai-engine.sh` in the new folder.
5. Once you're happy it works, delete the old folder.

→ [Full details](install-linux.md#updating-to-a-new-version)

Your account, chats, settings and downloaded models are all still there.

> Windows may show its security warning once more, because the `.exe` is a new file.
> → [What to click](install-windows.md#the-windows-smartscreen-warning)

---

## Version numbers

They look like `v0.1.0-rc.5.0` — higher is newer. Every build is marked **Pre-release** on GitHub,
which is expected for a beta.

Each release lists what changed. Skimming that is worthwhile: it tells you what's worth re-testing, and
whether something you previously reported is fixed.

---

## Getting told about new builds

Optional. GitHub can email you:

1. Click **Watch** at the top right of the repository front page.
2. **Custom** → tick **Releases** → **Apply**.

You'll be emailed for new releases only.

---

## Going back to an older version

Possible, with one real caveat.

> ### ⚠️ Back up your data folder first
>
> The database **upgrades** when you run a newer build, and those upgrades are **not reversed** when
> you go back. An older build may not be able to read a database a newer one has already upgraded.
>
> Copy `%LOCALAPPDATA%\XE-Local-AI-Engine` somewhere safe **before** downgrading.

Then download the older release and run it as in Option B.

If the older build won't start against your upgraded database, either restore your backup or go back to
the newer version. **Don't delete `node.sqlite` as a "fix"** — that's data loss, not a downgrade.

---

## Problems with an update

If a new build is worse than the one before — slower, broken, or it lost something — **that is exactly
the kind of thing worth reporting.** Please include which version worked and which doesn't.

→ [How to report it](feedback.md)

---

**[← Back to the main page](../README.md)**

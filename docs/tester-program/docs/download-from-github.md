# How to download the app from GitHub

If you have not used GitHub before, this page is for you. GitHub is a website built for programmers,
and the download you want is **not** the big green button — that is the single most common mistake.

**What you are looking for:** one file named `XE-Local-AI-Engine-win-Portable.zip`.

---

## First: sign in to GitHub

The builds are in a **private repository**. You can only see them while signed in with the GitHub
account I invited.

1. Go to [github.com](https://github.com) and **sign in**.
2. Check the invitation — GitHub sends it by email, and it also appears at
   [github.com/notifications](https://github.com/notifications). **You must accept it**, or the page
   will not open for you.

> **Seeing "404 — page not found"?** That is what GitHub shows for a private repository you cannot
> access. It does not mean the page is missing. Either you are signed out, signed in with a
> *different* account than the one I invited, or the invitation is still unaccepted. Tell me which
> GitHub username you are using and I'll check.

---

## Step 1 — Open the Releases page

**[→ Click here for the latest release](../../../releases/latest)**

You can also get there manually: on the repository's front page, look at the right-hand sidebar for
**"Releases"** and click it.

---

## Step 2 — Ignore the green "Code" button

> ### ⚠️ The green **`<> Code`** button is NOT the app
>
> That button downloads the *contents of this repository* — the instructions you are reading right
> now. It does **not** contain the program, and there will be nothing to run inside it.
>
> **If you downloaded something and found no `.exe` inside, this is what happened.** Come back to the
> Releases page.

---

## Step 3 — Find the newest release

The Releases page lists versions newest-first. The top entry is the one you want.

You will see labels next to it:

| Label | Meaning |
|---|---|
| **Pre-release** | Expected — every beta build here is marked this way. Not a problem. |
| **Latest** | The newest build. |

The version looks like `v0.1.0-rc.5.0`. Higher numbers are newer.

---

## Step 4 — Open "Assets"

Under the release notes there is a section called **Assets**. It is sometimes **collapsed**, showing
just a small ► triangle and a file count.

**Click the word "Assets"** (or the triangle) to expand it.

You will then see a list like this:

```
▼ Assets                                                    7

   📄  XE-Local-AI-Engine-win-Portable.zip                  ✅ WINDOWS — this one
   📄  XE-Local-AI-Engine-<version>-linux-Portable.zip      (Linux build)
   📄  XE-Local-AI-Engine-<version>-linux-Portable.zip.sha256   (Linux checksum)
   📄  XE-Local-AI-Engine-<version>-delta.nupkg             ❌ ignore
   📄  XE-Local-AI-Engine-<version>-full.nupkg              ❌ ignore
   📄  releases.win.json                                    ❌ ignore
   📄  RELEASES                                             ❌ ignore

   📄  Source code (zip)                                       ❌ ignore
   📄  Source code (tar.gz)                                    ❌ ignore
```

*(`<version>` is a placeholder — the real files carry the version number of the build you're viewing,
e.g. `v0.1.0-rc.5.0`. On **Linux**, grab the two `linux-Portable` files instead and follow the
[Linux guide](install-linux.md).)*

### Click **`XE-Local-AI-Engine-win-Portable.zip`**

That starts the download. It is around 100 MB, so give it a minute.

<details>
<summary><b>What are the other files?</b></summary>

- **`.nupkg` files** — used by the app's built-in updater when it upgrades itself. You never open these.
- **`releases.win.json` / `RELEASES`** — the list the updater reads to discover new versions.
- **"Source code (zip / tar.gz)"** — added automatically by GitHub to every release. For this project
  they contain **only these instruction pages**, not the application. There is nothing runnable inside.

Downloading the wrong one wastes time but does no harm.

</details>

---

## Step 5 — If your browser blocks the download

Browsers are suspicious of large `.exe`-containing ZIP files from sites they don't recognise.

**Microsoft Edge / Google Chrome** may show *"… is not commonly downloaded"* or *"blocked"*:

1. Open your **Downloads** list (`Ctrl` + `J`).
2. Find the blocked entry.
3. Click the **`⋯`** menu next to it → **Keep**.
4. If asked again, choose **Keep anyway** / **Show more** → **Keep anyway**.

**Firefox:** click the **`⋯`** next to the download → **Allow download**.

This is the same category of warning as the one Windows shows later, and it has the same cause: the
build is not code-signed. See [the SmartScreen section](install-windows.md#the-windows-smartscreen-warning).

---

## Step 6 — Check you got the right thing

The downloaded file should be:

- **Named** `XE-Local-AI-Engine-win-Portable.zip`
- **Roughly 90–100 MB**

If it is only a few hundred kilobytes, you downloaded the source code by mistake — go back to
[Step 2](#step-2--ignore-the-green-code-button).

<details>
<summary><b>Optional: verify the download is genuine</b></summary>

You can confirm your copy is byte-for-byte what I published.

**1. Get the expected value from GitHub.** GitHub records a **SHA-256 digest** for each release asset.
Depending on your GitHub layout it appears in the asset's download panel (open the `...` menu beside
the file) — it is **not** printed in the release notes text, so don't hunt for it there. This value is
published by GitHub from the stored file itself, so it always matches the exact build you're downloading.
If you can't find it in the interface, message me and I'll confirm the digest for your release.

**2. Compute the value on your machine.** Open PowerShell and run:

```powershell
Get-FileHash "$env:USERPROFILE\Downloads\XE-Local-AI-Engine-win-Portable.zip" -Algorithm SHA256
```

**3. Compare them.** They should match, ignoring upper/lower case and the `sha256:` prefix.

If yours does not match, **do not run the file** — please tell me.

> **Note:** the release notes themselves are a changelog and do **not** contain a checksum. The digest
> is published by GitHub on the asset itself, which is better — it is computed by GitHub from the
> stored file rather than typed in by me.

</details>

---

## Next

**→ [Installing on Windows](install-windows.md)** — unblocking, extracting, and getting past the
Windows security warning.

---

## Getting notified about new builds

Optional, but useful: GitHub can email you when I publish a new build.

1. On the repository front page, click **Watch** (top right).
2. Choose **Custom** → tick **Releases** → **Apply**.

You will then be emailed for new releases only, not for every change.

---

**[← Back to the main page](../README.md)**

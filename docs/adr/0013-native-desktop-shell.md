# ADR 0013: The native desktop is a separate thin shell process that supervises the engine, never a host it links against

- **Status:** Accepted — by the repository owner (`w0rldx`) on 2026-09-22.
- **Date:** 2026-09-22
- **Scope:** How the product presents a native window on Windows and Ubuntu: what the shell process is allowed to
  know about the engine, who owns the engine's lifetime, what closing the window means, what the embedded WebView
  may do, and how a self-update is coordinated. It changes nothing about the engine's own API surface, the SPA, the
  supervised inference runtimes, or browser and headless operation.
- **Authority:** Operator decisions recorded in
  [Native desktop 1.0](../roadmaps/native-desktop-1.0.md) §"Approved direction" and §"Production completion
  checkpoint — 2026-09-22".
- **Amends:** nothing.

## Context

1.0 requires a native desktop application, not a browser tab. The engine is already a self-contained ASP.NET Core
node that serves the SPA over loopback, speaks REST and SignalR, and supervises its own child runtimes. The cheapest
wrong answer would have been to link a window toolkit into that host: one process, direct object access, a JavaScript
bridge to the engine's internals.

That answer costs the product its other two modes. The same binary must still start headless on a server, still start
in browser mode, and still answer operator CLI commands — a host whose startup path constructs a GUI cannot. It also
doubles the application's API surface: every capability reachable through a bridge is a second, unauthenticated way
into the engine that the loopback key and the `/api/local/v1` shape do not cover.

The window and the engine therefore had to be separate processes, which makes engine ownership the real decision.
A desktop user expects closing the window to stop the work; an operator who started the engine themselves and then
opened the window expects the opposite. Both are legitimate and they cannot share one rule.

Two more constraints came from the platforms. Ubuntu's WebKitGTK gives an embedder fine-grained control over what a
document may do — and no usable tray on a stock GNOME session. Windows' WebView2 gives a working tray and far less
document-level control.

## Decision

1. **The shell is its own project (`XE-Local-AI-Engine.Desktop`) with no project reference to any engine assembly.**
   It is a thin Avalonia + NativeWebView window that points at the engine's loopback origin and nothing else. There
   is no JavaScript bridge to engine services and no second application API; the SPA keeps using REST, SignalR and
   normal authentication. Protocol literals the two processes share are duplicated deliberately and pinned by a
   cross-project contract test (`NativeDesktopContractTests`), because a shared assembly would reintroduce the
   coupling this decision exists to prevent.
2. **The shell owns an engine only when it started one.** `DesktopEngineSession` first discovers a healthy engine for
   the selected data root and attaches to it; only when there is none does it start the adjacent engine with
   `--desktop --no-browser`. Quit stops a shell-started engine and never an attached one.
3. **Ownership is carried by a named pipe, not by process parentage.** An owned engine connects
   `DesktopParentLifetime` before the host is constructed; losing the pipe requests graceful shutdown and arms a
   bounded exit watchdog, and the shell bounds its own wait on the engine before killing the tree. Neither side may
   wait indefinitely on the other.
4. **One shell per data root.** `DesktopInstance` holds a single-instance lease and an activation pipe for the same
   user; starting the application again restores the running window instead of opening a second one.
5. **The first close asks.** Keep in tray / Quit / Cancel, with an optional remembered choice that stays editable in
   desktop settings. Where the tray is unavailable — which is the case on Linux today — the shell refuses to hide its
   only window rather than leaving the user no way back.
6. **The Linux WebView runs under a restricted document policy; Windows does not.** The GTK adapter verifies the
   actual top-level document's CSP and Permissions-Policy, blocks embedded frames, grants microphone-only consent
   (camera and display capture require browser mode) and bounds blob export through a validated Save dialog. On
   Windows the shell relies on WebView2's defaults and applies none of this.
7. **Browser, headless, MCP-only and operator CLI modes bypass the window entirely** and keep their current
   behaviour, including the standalone bootstrap's browser launch.
8. **A self-update is coordinated through whichever process supervises the engine** — the shell on Linux via
   `XE_DESKTOP_SUPERVISOR_PID`, the launcher on Windows — so Velopack restarts the executable the user actually
   started. An attached standalone engine refuses an in-app update while a shell holds the desktop lease, and says
   so: close the window and update from the browser.

## Consequences

- **The asymmetry in decision 6 is real and unequal.** A Linux user gets a document policy the engine and the shell
  both enforce; a Windows user gets WebView2's defaults. The Windows side is not hardened to the same level, and
  claiming otherwise would be false. Closing that gap is a later decision, not an edit to this record.
- **Every shared literal is a duplication that can drift.** The contract test is the only thing holding the two
  processes' protocol together; deleting or weakening it silently re-opens a class of mismatch that no build error
  would catch.
- **Attachment makes "is the engine mine?" a runtime answer**, so window-close behaviour differs between two
  outwardly identical launches. This is deliberate, and it is why Quit warns about interrupting work generally
  rather than enumerating activity.
- **Real Ubuntu X11/Wayland acceptance was waived for this delivery.** WSLg observations are supplemental and are not
  equivalent. This is a validation gap on a shipped surface, not a pass; native Ubuntu delivery itself remains
  required.
- **No tray on Linux means a Linux user has no Keep-in-tray option**, only Quit or Cancel, until a restore path
  exists that a stock GNOME session honours.
- **Packaging ships one tree.** The shell publishes into the same output directory as the engine payload so Velopack
  packs a single install; the release workflow, `package-rc.sh` and `package-tester-win.ps1` all depend on that.
- **The engine keeps working with no shell at all.** Headless, browser and MCP-only are unaffected by everything
  above, which is the property that made the separate-process shape worth its cost.

## Alternatives considered

- **Link the window toolkit into the engine host.** One process, no pipes, no duplicated literals. Rejected: it
  removes headless and server operation from the same binary and makes the GUI a startup dependency of a service.
- **Expose engine services to the WebView through a JavaScript bridge.** Rejected: it creates a second application
  API outside `/api/local/v1`, unauthenticated by construction, that every later feature would be tempted to use.
- **Share a small contracts assembly between the shell and the engine.** Rejected for now: the shell would then carry
  an engine project reference, which is the coupling decision 1 forbids; a test that asserts the duplicated literals
  match buys the same protection at a lower structural cost.
- **Always own the engine, or never own it.** Rejected both ways: always-own kills an operator's own long-running
  node when a window closes, never-own leaves an orphaned engine after every desktop session.
- **Hide the window to a tray icon on Linux anyway.** Rejected: on a session with no usable tray the user is left
  with a running process and no way to reach it.
- **Electron or a bundled Chromium.** Rejected: it duplicates a browser engine the product already reaches through
  the OS WebView, for a shell whose entire job is to show one loopback origin.

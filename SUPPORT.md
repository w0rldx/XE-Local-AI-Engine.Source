# Getting help

XE Local AI Engine is maintained in spare time. There is no paid support and no guaranteed response time, but every
report is read.

## Before you ask

- **Using the app:** the [FAQ & troubleshooting](docs/user-guide/docs/faq.md) page answers the common problems
  (installing, the Windows warning, slow replies, the unlock page, resets, updates).
- **Operating or developing it:** [Troubleshooting](docs/troubleshooting.md) covers the data directory, exit codes,
  logs and the support bundle.

## Where to ask

| You have | Go to |
| --- | --- |
| A bug or a problem | [Open an issue](https://github.com/w0rldx/XE-Local-AI-Engine.Source/issues/new/choose) and pick the bug form |
| A question, an idea or an impression | [GitHub Discussions](https://github.com/w0rldx/XE-Local-AI-Engine.Source/discussions), or the feedback form under [issues](https://github.com/w0rldx/XE-Local-AI-Engine.Source/issues/new/choose) |
| A security vulnerability | **Not a public issue.** Report it privately as described in [SECURITY.md](SECURITY.md) |

## What to include

[Giving feedback](docs/user-guide/docs/feedback.md) explains the best report in detail. In short:

- **The version.** It is on the **About** page in the app. From a terminal, `--status --json` reports it as `version`
  while the engine runs (on Windows, through `current\XE-Local-AI-Engine.WindowsLauncher.exe`).
- **The support bundle.** The bug button in the header opens **Diagnostics**; **Export** saves one zip with the
  browser snapshot and the engine's log tails. Review it before attaching it: the scrubbing is a safety net, not a
  guarantee.
- **If the app will not start:** the exit code and message, and the newest files from the `logs` folder in the data
  directory.
- What you did, what happened, and what you expected.

Never post passwords, the recovery code, `node.key` or private documents in an issue: issues are public.

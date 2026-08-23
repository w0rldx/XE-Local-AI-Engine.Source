# Live evaluation lane merge — 2026-07-31

This note preserves a report that previously existed only in a 2026-07-31
commit message and was moved into the documentation record during repository
history maintenance. It is a point-in-time record, not a statement about
current behavior.

---

Four lanes covered the remaining surface. Only one could hold the browser
and GPU at a time — a second lane restarting the node or driving Chrome
would have corrupted the first's results — so image generation ran live
and the rest are code-grounded with live-test recipes.

Image generation works end to end: SD1.5 installed from the download
card, 512x512/20 steps in 9s warm at 88–92% GPU, seeded output
byte-identical across runs, cancel releases VRAM within 2s, and history
is encrypted at rest. Two P1s: the job card reports the requested size
rather than the actual PNG size (sd-cpp rounds to a multiple of 64, and
the form accepts any width with no step constraint), and a failed model
download is silent forever — 202 Accepted, an optimistic toast, then
nothing, leaving an orphan directory.

Development Mode is feature-incomplete rather than unsafe: the Docker
provider is merged and the role seam is compiler-enforced, but operator
image acquisition (S3.3) is unbuilt, so nothing can register an image.
Docker is installed and running here; that is not the blocker.

Speculative decoding's settings are seeded once at host startup and never
re-read, so its "Applies after the node restarts" hint is accurate — the
instructive contrast with the tool-capable allowlist, which shares the
pattern but says nothing.

Two lane claims are recorded as rejected or cautioned rather than
adopted. The MTP-draft-undiscoverable finding does not hold: the local
model store is flat, so TopDirectoryOnly has no subfolder to miss, and
the quant parser accepts the draft filename. Correcting it surfaced the
real issue — the registry has no notion of a draft, so a downloaded MTP
draft lists as an ordinary chat model. And the "production-ready, no
gotchas" verdict on sub-agent spawn and Open Canvas came from code
reading with nothing run, so it is unverified rather than verified.

Report-only commit — no product code changed.

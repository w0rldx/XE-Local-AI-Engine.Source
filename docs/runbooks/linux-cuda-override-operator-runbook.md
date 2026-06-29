# Linux CUDA bring-your-own llama-server — operator runbook

**Date:** 2026-06-29
**Audience:** operator running the engine on a Linux + NVIDIA host who wants the **CUDA** inference path (not the default Vulkan fallback).
**Feature:** `[[linux-cuda-byo-override]]` — see plan `2026-06-29-linux-cuda-byo-override-plan.md`. Code committed `e59cbc43`.

---

## Why this exists

Upstream llama.cpp (`ggml-org/llama.cpp`) ships **no Linux CUDA prebuilt** — only Windows gets a CUDA build. So on a Linux NVIDIA box the engine deliberately falls back to **Vulkan** (`GpuVariantSelector`). This override lets you point the engine at a **locally-built CUDA `llama-server`** so the CUDA path can be exercised before deploy.

It is **off by default**. When the override env var is unset, acquisition behaves exactly as today (pinned download + SHA256 verify). The override **skips** the download + hash step (an operator-built binary has no publisher digest) and instead validates the binary you supply.

> **No-build-knowledge alternative — in-app CUDA build.** If you have the toolchain installed (nvcc/cmake/gcc/g++/make-or-ninja/git + an NVIDIA driver + free disk) but do not want to hand-build llama.cpp, the engine can build it for you: **Node Settings ▸ llama.cpp runtime ▸ "CUDA (build from source)"** (developer-mode / opt-in gated, Linux only). It clones the engine's **pinned** llama.cpp tag, verifies the checked-out commit equals the pinned SHA, builds a CUDA `llama-server` under a scrubbed environment, validates it, and **adopts it as a managed CUDA runtime** that is then selected automatically — no env var, survives restart, removable/rebuildable from the same card. The build option is disabled with an itemized checklist when any prerequisite is missing. See plan `2026-06-29-linux-cuda-inapp-build-plan.md`. The bring-your-own override below remains the manual alternative (and the fallback when the in-app build is too fragile across distros).

> **WSL caveat:** a WSL2 instance with **no GPU passthrough cannot run this path** — the `--list-devices` GPU check will (correctly) reject the binary. You need a real Linux+NVIDIA host, or WSL2 with NVIDIA GPU passthrough configured.

---

## Prerequisites (host)

- **NVIDIA driver** installed and working (`nvidia-smi` lists your GPU).
- **CUDA toolkit** (for building) and the **CUDA runtime libraries** (`libcudart.so`, `libcublas.so`) reachable at run time — normally satisfied by a system-wide CUDA install, or via `LD_LIBRARY_PATH` (see "Shared libraries" below).
- **CMake** + a C/C++ toolchain to build llama.cpp.
- **glibc ≥ 2.33** for the file-ownership check to be enforced (older glibc → the ownership control is silently skipped; the non-world-writable + exec + smoke + GPU checks still apply — see "Security model").

---

## Step 1 — Build a CUDA `llama-server`

Build the **same llama.cpp version the engine is pinned to** where practical (current pin: tag `b9692`; check `LlamaCppReleasePins.PinnedTag`). Mismatched versions usually work but are not guaranteed.

```bash
git clone https://github.com/ggml-org/llama.cpp.git
cd llama.cpp
git checkout b9692                 # match the engine's pinned tag when possible
cmake -B build -DGGML_CUDA=ON -DCMAKE_BUILD_TYPE=Release
cmake --build build --config Release -t llama-server -j
# → build/bin/llama-server
```

Sanity-check the build yourself before wiring it in:

```bash
./build/bin/llama-server --version          # prints a version banner, exits 0
./build/bin/llama-server --list-devices     # must list a CUDA device with a "(NNNN MiB, NNNN MiB free)" column
```

If `--list-devices` shows **no** GPU device, the build did not link CUDA (or the driver/runtime is missing) — fix that first; the engine will reject a CUDA-tagged binary that exposes no GPU.

---

## Step 2 — Place the binary securely

The override skips hash verification, so the engine enforces **filesystem trust** instead. Put the binary in an **operator-owned, non-world-writable** directory:

```bash
sudo mkdir -p /opt/llama-cuda/bin
sudo cp llama.cpp/build/bin/llama-server /opt/llama-cuda/bin/
sudo chmod 755 /opt/llama-cuda/bin/llama-server      # exec bit set, NOT world-writable
sudo chmod 755 /opt/llama-cuda/bin                   # parent dir NOT world-writable
# owner must be the user that runs the engine, or root
```

Requirements the engine checks (all must pass, Linux):
- absolute path to an **existing regular file** (not a symlink-to-dir, device, or FIFO),
- the binary **and its parent directory** are **not world-writable** (`o-w`),
- the binary has an **exec bit**,
- the binary **and parent dir** are owned by the **running user (euid)** or **root**.

> Do **not** place the binary in a world-writable location like `/tmp` — it will be rejected (a world-writable path is the swap-attack surface the hash check used to cover).

---

## Step 3 — Set the environment variables

The override is read **only** from process environment variables (operator-trust channel) — never from app config files or the UI.

| Variable | Required | Value |
|---|---|---|
| `XE_LLAMACPP_SERVER_PATH` | yes (enables the override) | absolute path to your `llama-server`, e.g. `/opt/llama-cuda/bin/llama-server` |
| `XE_LLAMACPP_VARIANT` | no | `cuda` (default), `vulkan`, or `cpu` — case-insensitive. Defaults to `cuda` when unset. An **unrecognized** value fails startup. |

```bash
export XE_LLAMACPP_SERVER_PATH=/opt/llama-cuda/bin/llama-server
export XE_LLAMACPP_VARIANT=cuda
```

**Shared libraries:** the engine spawns `llama-server` with the **inherited parent environment**, so if your CUDA runtime libs aren't on the default loader path, export `LD_LIBRARY_PATH` in the **same shell** before launching the engine:

```bash
export LD_LIBRARY_PATH=/usr/local/cuda/lib64:$LD_LIBRARY_PATH
```

> The variant **must match the binary's build**. If you set `cuda` but supply a CPU/Vulkan binary, the `--list-devices` GPU check rejects it (no silent CPU run). If you set `cpu`, the GPU-device check is skipped.

---

## Step 4 — Launch & verify

1. Start the engine from the shell where the env vars are exported.
2. **Startup log** — confirm the Warning appears once:
   > `Using operator-supplied llama-server at /opt/llama-cuda/bin/llama-server (variant Cuda); integrity hash verification is skipped.`
   No warning = the override was not picked up (env var not exported in this process). 
3. **Runtime version card** (Model management → llama.cpp version) shows the version as `override`.
4. **Run a chat** with a local GGUF model and confirm GPU offload:
   - `nvidia-smi` shows VRAM usage rise while the model loads,
   - the spawned `llama-server` logs show CUDA devices + offloaded layers,
   - the launch args include `-ngl` / `--fit` (GPU placement — only emitted for non-CPU variants).

### Validation checklist
- [ ] `nvidia-smi` lists the GPU on the host.
- [ ] `<binary> --list-devices` shows a CUDA device locally (Step 1).
- [ ] Startup Warning logged once.
- [ ] Version card reads `override`.
- [ ] Chat serves with VRAM consumed (GPU, not CPU).

---

## Behavior notes & gotchas

- **Runtime updates are disabled under the override.** The "update llama.cpp" endpoint returns **409** with "Runtime updates are disabled while a bring-your-own llama-server override is active; the operator manages the binary." Unset the override to manage pinned runtimes again.
- **Hard-killed override server is NOT auto-reaped.** The startup orphan reaper only matches binaries under the engine's own cache root. An override `llama-server` left running after a hard stop (e.g. `aspire stop`, see `[[aspire-stop-hang-llama-orphan]]`) holds its port + VRAM — **kill it manually**: `pkill -f /opt/llama-cuda/bin/llama-server`.
- **Do not run the engine elevated** (root / privileged service account) while an override is set — the override binary executes at the engine's privilege and inherits its full environment (`LD_LIBRARY_PATH`, `LD_PRELOAD`, …).
- **glibc < 2.33:** the ownership check degrades to the managed permission checks only (ownership not enforced); the non-world-writable + exec + smoke + GPU-device checks still apply.
- **Off by default:** unset `XE_LLAMACPP_SERVER_PATH` and the engine reverts to pinned download/verify with zero behavioral change.

---

## Troubleshooting

| Symptom / error | Likely cause | Fix |
|---|---|---|
| No startup Warning; runs on Vulkan | env var not in the engine's process | export `XE_LLAMACPP_SERVER_PATH` in the launching shell/service unit |
| "must be an absolute path" | relative path supplied | use a full path starting with `/` |
| "does not point to an existing file" / "not a regular file" | wrong path, or a symlink-to-dir/device | point at the real `llama-server` regular file |
| "is world-writable; tighten its permissions" | binary or parent dir is `o+w` | `chmod o-w` the binary **and** its directory |
| "is not marked executable" | missing exec bit | `chmod +x` the binary |
| "not owned by the operator running the application" | foreign-uid owner (glibc ≥ 2.33) | `chown` to the running user, or place under a root-owned dir |
| "failed its self-check" | binary can't run (`--version`): missing runtime libs / wrong arch | install CUDA runtime, set `LD_LIBRARY_PATH`, rebuild for the host arch |
| "exposes no GPU device for the requested acceleration variant" | non-CUDA binary tagged `cuda`, or driver/runtime missing | rebuild with `-DGGML_CUDA=ON`, fix the driver, or set `XE_LLAMACPP_VARIANT` to match the real build |
| Startup fails: "unrecognized llama.cpp acceleration variant" | typo in `XE_LLAMACPP_VARIANT` | use `cpu`, `cuda`, or `vulkan` |

---

## Turning it off

```bash
unset XE_LLAMACPP_SERVER_PATH    # (and XE_LLAMACPP_VARIANT)
```
Restart the engine — it returns to the pinned download/verify path. Re-enabling runtime updates (409 lifts) is automatic once unset.

---

## Verification commands (quick reference)

```bash
nvidia-smi                                            # host GPU present
$XE_LLAMACPP_SERVER_PATH --version                    # binary runs
$XE_LLAMACPP_SERVER_PATH --list-devices               # CUDA device listed
ls -ld $XE_LLAMACPP_SERVER_PATH "$(dirname $XE_LLAMACPP_SERVER_PATH)"   # perms: not o+w, exec bit, owner
pkill -f "$XE_LLAMACPP_SERVER_PATH"                   # manual cleanup of a stuck override server
```

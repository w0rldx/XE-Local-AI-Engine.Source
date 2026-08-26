# Linux CUDA bring-your-own llama-server — operator runbook

**Date:** 2026-06-29
**Last validated against the repository:** 2026-08-08 (`9405df91`)
**Audience:** operator running the engine on a Linux + NVIDIA host who wants the **CUDA** inference path (not the default Vulkan fallback).
**Authoritative sources:** the [environment contract](../../XE-Local-AI-Engine.Providers.LlamaServer/Configuration/LlamaServerRuntimeOverrideOptions.cs), [binary validation path](../../XE-Local-AI-Engine.Providers.LlamaServer/Implementation/LlamaCppBinaryManager.Override.cs), [current llama.cpp pin](../../XE-Local-AI-Engine.Providers.LlamaServer/LlamaCppReleasePins.cs), and [live GPU smoke](../../scripts/run-gpu-smoke-local.sh).

---

## Why this exists

Upstream llama.cpp (`ggml-org/llama.cpp`) ships **no Linux CUDA prebuilt** — only Windows gets a CUDA build. So on a Linux NVIDIA box the engine deliberately falls back to **Vulkan** (`GpuVariantSelector`). This override lets you point the engine at a **locally-built CUDA `llama-server`** for an operator-managed CUDA runtime.

It is **off by default**. When the override environment variable is unset, acquisition uses the pinned download and SHA256 verification path. The override **skips** download and hash verification (an operator-built binary has no publisher digest) and instead validates the binary you supply.

> **Preferred managed alternative — in-app CUDA build.** If you have the toolchain installed (nvcc/cmake/gcc/g++/make-or-ninja/git + an NVIDIA driver + free disk) but do not want to hand-build llama.cpp, use **Node Settings ▸ llama.cpp runtime ▸ "CUDA (build from source)"** (developer-mode / opt-in gated, Linux only). It clones the engine's pinned tag, verifies the checked-out commit equals `LlamaCppReleasePins.PinnedSourceCommitSha`, builds under a scrubbed environment, validates the result, and adopts it as a managed CUDA runtime. It needs no environment override, survives restart, appears in runtime status, and can be removed or rebuilt from the same card. The build option shows an itemized prerequisite checklist when unavailable. The bring-your-own override below remains the operator-managed alternative.

### Choose one ownership model

| | Managed in-app source build | Bring-your-own override (this runbook) |
|---|---|---|
| Selection | Installed runtime record; selected automatically | `XE_LLAMACPP_SERVER_PATH` process environment |
| Source/version | Engine pin and verified source commit | Operator chooses and builds, preferably from the engine pin |
| Integrity | Source identity plus managed install validation | Filesystem trust checks and runtime self/device probes; no publisher digest |
| Updates | Remove/rebuild from Node Settings when the engine pin changes | Operator replaces the binary; in-app runtime updates return 409 while override is active |
| Removal | Eject/remove from the runtime card | Unset the environment variable and restart |
| Orphan cleanup | Engine-owned runtime paths participate in managed cleanup | An externally located orphan may require operator cleanup after an unclean host termination |

> **WSL caveat:** a WSL2 instance with **no GPU passthrough cannot run this path** — the `--list-devices` GPU check will (correctly) reject the binary. You need a real Linux+NVIDIA host, or WSL2 with NVIDIA GPU passthrough configured.

---

## Prerequisites (host)

- **NVIDIA driver** installed and working (`nvidia-smi` lists your GPU).
- **CUDA toolkit** (for building) and the **CUDA runtime libraries** (`libcudart.so`, `libcublas.so`) reachable at run time — normally satisfied by a system-wide CUDA install, or via `LD_LIBRARY_PATH` (see "Shared libraries" below).
- **CMake** + a C/C++ toolchain to build llama.cpp.
- **glibc ≥ 2.33** for the file-ownership check to be enforced (older glibc → the ownership control is silently skipped; the non-world-writable + exec + smoke + GPU checks still apply — see "Security model").

---

## Step 1 — Build a CUDA `llama-server`

Build the **same llama.cpp version the engine is pinned to**. This is not a nicety: a pin older than a model's architecture produces a GPU runtime that simply refuses to load that model. Read the tag out of the source rather than copying one from this page — a literal here goes stale silently, and this runbook has already shipped an unusable tag once.

```bash
# Resolve the engine's pinned tag from the source of truth. Run from the repo root.
LLAMACPP_TAG=$(grep -oP 'PinnedTag\s*=\s*"\K[^"]+' \
  XE-Local-AI-Engine.Providers.LlamaServer/LlamaCppReleasePins.cs)
echo "engine pin: $LLAMACPP_TAG"     # non-empty, looks like bNNNNN

git clone https://github.com/ggml-org/llama.cpp.git
cd llama.cpp
git checkout "$LLAMACPP_TAG"       # match the engine's pinned tag
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
- **An externally located orphan is not covered by the startup path-based reaper.** Normal supervised shutdown still owns the launched child, but an override `llama-server` left running after an unclean host termination can hold its port and VRAM. Identify the exact PID, verify both `/proc/<pid>/exe` and `/proc/<pid>/cmdline` point to the abandoned override process, then terminate only that verified PID with `kill -- <pid>`. Never use a substring-wide `pkill -f`; it can terminate unrelated shells, test harnesses, or runtime processes.
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
ps -eo pid=,args= | grep '[l]lama-server'             # identify candidates; do not kill by substring
readlink -f /proc/<verified-pid>/exe                  # must equal the configured override binary
tr '\0' ' ' < /proc/<verified-pid>/cmdline; echo     # verify the full command line
kill -- <verified-pid>                                # terminate only the verified abandoned process
```

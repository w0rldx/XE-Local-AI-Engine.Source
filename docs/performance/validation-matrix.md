# Local inference performance validation matrix

This matrix is intentionally conservative. “Unsupported here” means that the
current development machine cannot produce transferable evidence; it does not mean
the product backend is unsupported.

| Target | Status on current box | Evidence that can be captured | Required follow-up |
|---|---|---|---|
| WSL2 + managed source-build CUDA | Supported for baseline/rebaseline and fit/replay; **not sufficient for the corrected Lane 4 memory gate** | Runtime/helper hashes, tag, device list, baseline/rebaseline, fit/replay vectors, global VRAM | Reject samples with WDDM paging or global/process divergence; use another host for Lane 4 because WSL exposes no PID-scoped CUDA residency rows |
| WSL2 CPU-only spawn | Supported | Baseline/rebaseline and fit/helper availability without GPU placement claims | Keep CPU evidence separate from GPU claims |
| Native Windows + NVIDIA/WDDM | Manual only | `capture_windows_vram.ps1` idle/game fixtures; operator fit/helper availability | Return both fixtures and exact binary hashes; no automated certification |
| Linux NVIDIA Vulkan | Unsupported here | `--list-devices` records zero devices because no NVIDIA Vulkan ICD is installed | Capture on a machine with a working NVIDIA Vulkan ICD; do not infer from CUDA |
| Native Linux CUDA | Unsupported here | None: WSL2/WDDM memory and OOM behavior is not native Linux behavior | Capture on a native Linux NVIDIA host, including hard-OOM recovery |
| AMD or Intel GPU | Unsupported here | None | Capture backend/device list, fit/replay, throughput, and memory on representative hardware |
| 8 GB / constrained VRAM | Unvalidated | WDDM ballast does not constrain the process-budget reader used by fit | Use hardware or a technique proven to alter the exact free-VRAM query consumed by fit |
| CUDA OOM recovery | Unsafe/unvalidated here | WDDM pages to host RAM and can destabilize the host | Exercise on a disposable native-Linux GPU host |
| MoE placement | Excluded from fixed model set | No transferable placement evidence | Add a separate fixed MoE model/corpus experiment before making MoE claims |
| BYO runtime | Capability-dependent | Binary/tag/provenance/hash and helper probe | Missing `llama-fit-params` remains an explicit unsupported capability |
| Pinned prebuilt runtime | Capability-dependent | Binary/tag/provenance/hash and helper probe | Record whether the asset contains the sibling helper |
| Managed CUDA source build | Supported | Both `llama-server` and `llama-fit-params` must be retained and hashed | Re-run after any upstream tag or build-option change |

No result from one row certifies another. In particular, WSL2 CUDA throughput does
not certify Vulkan, native Linux OOM, native Windows contention, or 8 GB hardware.

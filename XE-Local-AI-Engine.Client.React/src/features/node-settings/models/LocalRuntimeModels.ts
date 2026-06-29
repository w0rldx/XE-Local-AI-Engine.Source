// Domain view-models for the local-runtime cards on the Node Settings page (relocated from the model-fit advisor).
// The generated types are the single source of truth for the wire shape; the mapper coalesces their optional fields.

// Known llama.cpp binary variants. cpu is the always-available fallback; cuda/vulkan are GPU-accelerated.
export type LlamaCppVariant = "cpu" | "cuda" | "vulkan";

export const llamaCppVariants: readonly LlamaCppVariant[] = ["cpu", "cuda", "vulkan"];

// The installed llama.cpp runtime, as recorded after a verified install. `installedAtUtc` is epoch milliseconds
// (long on the wire); undefined when nothing has been installed yet (first run resolves from the pin floor).
export interface LlamaCppInstalledRuntime {
	readonly tag: string;
	readonly variant: LlamaCppVariant | string;
	readonly asset: string;
	readonly installedAtUtc: number | undefined;
	// True when this runtime was produced by the in-app build-from-source CUDA path (a managed source build) rather than
	// a downloaded prebuilt asset. Optional so payloads/test fixtures predating the field still satisfy the shape.
	readonly isSourceBuild?: boolean;
}

// Domain view-model for the read-only runtime status (GET model-fit/llamacpp/runtime). Drives the updater panel and
// the global "update available" banner. `installed` is null when no runtime has been installed yet. `updateAvailable`
// is the single authoritative flag the UI keys off (the backend compares installed vs recommended). `isOffline` flags
// that the GitHub release API was unreachable — the recommended tag is still served from cache/pins, but the update
// button is disabled. `upstreamLatestTag` is resolved server-side but only surfaced in the UI under developer mode.
// `runningProcessCount` is the number of live llama-server child processes (the supervisor is the source of truth);
// while it is > 0 the runtime binary is in use, so the UI disables the install buttons until the operator ejects them.
export interface LlamaCppRuntimeStatus {
	readonly installed: LlamaCppInstalledRuntime | null;
	readonly recommendedTag: string;
	readonly upstreamLatestTag: string | null;
	readonly updateAvailable: boolean;
	readonly isOffline: boolean;
	readonly runningProcessCount: number;
	// True when the active runtime is an in-app source build (managed CUDA). The download-update flow is suppressed for
	// source builds; `rebuildAvailable` is set instead when the recorded build is stale versus the pinned source tag.
	// Optional so payloads/test fixtures predating these fields still satisfy the shape.
	readonly isSourceBuild?: boolean;
	readonly rebuildAvailable?: boolean;
}

// Domain view-model for a single CUDA build prerequisite (one host capability check: e.g. Linux, nvcc, cmake, NVIDIA
// GPU). `satisfied` drives the ✓/✗ glyph; `detail` is an operator-facing reason/version string.
export interface CudaBuildPrerequisiteItem {
	readonly key: string;
	readonly satisfied: boolean;
	readonly detail: string;
}

// Domain view-model for the CUDA build prerequisite report. `canBuild` is the authoritative gate the UI keys off for the
// "Build CUDA" button; `items` is the checklist. Linux is derived from the `os-is-linux` item (see CudaBuild helpers).
export interface CudaBuildPrerequisites {
	readonly items: readonly CudaBuildPrerequisiteItem[];
	readonly canBuild: boolean;
}

// Domain view-model for the persisted CUDA build status (GET cuda-build/status). Survives a client reconnect: a page
// reload mid-build re-reads `phase` + `logLines` here, and the live SignalR hub appends subsequent deltas on top.
export interface CudaBuildStatus {
	readonly phase: string;
	readonly isRunning: boolean;
	readonly terminal: boolean;
	readonly logLines: readonly string[];
	readonly sanitizedError: string | null;
	readonly tag: string | null;
}

// The prerequisite key the backend reports for the OS gate. The card is Linux-gated off this item (Locked #9): the CUDA
// build path is Linux-only, so a non-Linux host shows the card disabled with the unsatisfied reasons.
export const cudaBuildOsLinuxKey = "os-is-linux";

// Derives whether the host is Linux from the prerequisite report. True only when the `os-is-linux` item is satisfied.
export function isLinuxHost(prerequisites: CudaBuildPrerequisites | undefined): boolean {
	return prerequisites?.items.some((item) => item.key === cudaBuildOsLinuxKey && item.satisfied) ?? false;
}

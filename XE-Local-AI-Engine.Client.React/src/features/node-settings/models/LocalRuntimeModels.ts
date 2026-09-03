// Domain models for local-runtime settings; LocalRuntimeMappers normalizes optional generated wire fields.

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
	readonly sourceRepository?: string | null;
	readonly sourceCommit?: string | null;
	readonly sourceSelection?: "official" | "custom" | null;
	readonly sourceRevisionMode?: "enginePinned" | "defaultBranch" | "explicitCommit" | null;
	readonly sourceRequestedCommit?: string | null;
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

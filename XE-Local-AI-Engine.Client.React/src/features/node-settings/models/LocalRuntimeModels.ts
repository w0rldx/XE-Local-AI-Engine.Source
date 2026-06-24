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
}

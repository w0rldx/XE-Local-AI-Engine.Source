// Domain view-models for the Inference Optimizer operator surface (Lane C3). The optimizer explores tuned
// llama.cpp launch profiles, benchmarks them, and freezes the good ones for repeat use. The operator surface
// shows OUTCOMES only — status, tokens/s, VRAM — and deliberately never carries the raw launch flags
// (-ngl / -ot / -ts / tensor split / kv types / flash-attn) the backend tuned. Those fields exist on the wire
// DTO but are intentionally dropped by the mapper so they can never reach the UI. No machine key is present on
// the wire (local-only by design); the view-model has no slot for one either.

// The three terminal states a profile can be in. Discriminated union (lowercase) narrowed from the backend's
// PascalCase status string by the mapper, so an unknown future status normalizes to "explored" rather than
// smuggling an out-of-union value into a badge map.
export type InferenceProfileStatus = "explored" | "frozen" | "stale";

// Sanitized projection of one inference profile row. Carries only outcome-shaped fields: identity, role, the
// backend it targets, the terminal status, the chosen quant + context size, the MoE shape, whether a benchmark
// exists (gates Freeze), and the free-VRAM figure captured at freeze time (the only VRAM the list DTO reports).
// Raw llama.cpp launch flags are NOT mapped — see file header.
export interface InferenceProfileView {
	readonly id: string;
	readonly modelName: string;
	readonly role: string | null;
	readonly backend: string;
	readonly status: InferenceProfileStatus;
	readonly quant: string | null;
	readonly ctxSize: number | null;
	readonly isMoe: boolean;
	readonly expertCount: number | null;
	// True when the profile has a benchmark snapshot — the gate the UI uses to enable Freeze.
	readonly hasBenchmark: boolean;
	// Free VRAM (bytes) captured when the profile was frozen; null when absent / not frozen. The only VRAM figure
	// the list DTO carries (the live benchmark metrics arrive separately from a benchmark run).
	readonly frozenVramBytes: number | null;
}

// One benchmark's metrics, all nullable because the runner may omit a figure (e.g. no tool-loop on a non-agent
// run, no cache-hit when the prefix cache is cold). cacheHitRate is a 0..1 ratio; the card renders it as a %.
export interface InferenceBenchmarkMetrics {
	readonly tokensPerSecond: number | null;
	readonly ppTokensPerSecond: number | null;
	readonly ttftMs: number | null;
	readonly totalLatencyMs: number | null;
	readonly cacheHitRate: number | null;
	readonly toolLoopMs: number | null;
	readonly vramLoadBytes: number | null;
	readonly vramAfterBytes: number | null;
	readonly runs: number | null;
}

// The result of a benchmark run: the snapshot id, its metrics (or null when the run produced none), and the
// refreshed profile view. The panel keeps this in ephemeral component state to enrich the row's outcome line and
// render the metrics card; it is never mirrored into a store (server state lives in TanStack Query).
export interface InferenceBenchmarkResult {
	readonly snapshotId: string | null;
	readonly metrics: InferenceBenchmarkMetrics | null;
	readonly profile: InferenceProfileView | null;
}

// Narrows the wire status string to the domain union. Anything outside the known set falls back to "explored".
export function toInferenceProfileStatus(status: string | undefined): InferenceProfileStatus {
	switch (status) {
		case "Frozen":
			return "frozen";
		case "Stale":
			return "stale";
		default:
			return "explored";
	}
}

// Composes the compact outcome summary shown on a profile row — e.g. "42 tok/s · 6.2 GB". Pure (no i18n): the
// numeric parts carry conventional unit symbols (tok/s, GB) exactly like the sibling ModelFitFormatters, while
// the localized status word ("Frozen") is prepended by the component. Returns "" when no metric is available.
export function formatProfileOutcomeSummary(tokensPerSecond: number | null, vramBytes: number | null): string {
	const parts: string[] = [];
	if (tokensPerSecond !== null) {
		parts.push(`${tokensPerSecond.toFixed(0)} tok/s`);
	}
	if (vramBytes !== null) {
		parts.push(`${(vramBytes / 1024 ** 3).toFixed(1)} GB`);
	}
	return parts.join(" · ");
}

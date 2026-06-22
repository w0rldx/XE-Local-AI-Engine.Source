// Domain view-models for the local-runtime cards on the Node Settings page (relocated from the model-fit advisor).
// The generated types are the single source of truth for the wire shape; the mapper coalesces their optional fields.

// Known llama.cpp binary variants. cpu is the always-available fallback; cuda/vulkan are GPU-accelerated.
export type LlamaCppVariant = "cpu" | "cuda" | "vulkan";

export const llamaCppVariants: readonly LlamaCppVariant[] = ["cpu", "cuda", "vulkan"];

// Domain view-model for the resolved llama.cpp binary. isPinnedFallback flags that the active binary is the pinned
// fallback tag rather than a freshly resolved upstream release.
export interface LlamaCppVersion {
	readonly version: string;
	readonly variant: LlamaCppVariant | string;
	readonly isPinnedFallback: boolean;
	readonly pinnedTag: string;
}

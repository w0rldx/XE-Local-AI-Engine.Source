// Domain view-models for the Hugging Face GGUF browse + download flow on the Model Management page. Relocated from
// the model-fit advisor: the GGUF browse/download surface lives alongside the existing "Pull model" flow because it
// is a model-acquisition action, not hardware advice. The generated types are the single source of truth for the
// wire shape; the mappers coalesce their optional fields into these stricter shapes.

// Default quant the download flow requests when the operator does not pick a specific file (HF policy, Q4_K_M).
export const defaultGgufQuant = "Q4_K_M";

// Domain view-model for one HF GGUF repository candidate the browse search returns. Sanitized metadata only — no
// download URL or token. hasUsableGguf flags whether the repo actually exposes a downloadable GGUF file.
export interface GgufRepository {
	readonly repoId: string;
	readonly isGated: boolean;
	readonly downloads: number;
	readonly likes: number;
	readonly lastModifiedAtUtc: number | null;
	readonly license: string | null;
	readonly hasUsableGguf: boolean;
	// True when the repo's publisher is a known reputable GGUF packager / first-party org; false for an unknown or
	// community publisher. Never a filter — untrusted repos still render, but the browse list flags them with a warning badge.
	readonly isTrustedPublisher: boolean;
}

// Static quality tier the backend classifier assigns to a quant (no hardware involved). Ordered best→smallest:
// NearLossless (Q8_0/Q6_K/F16…), SweetSpot (Q5_K_*), Balanced (Q4_K_*), Small (Q3*/IQ3/IQ4/legacy), Minimal (Q2*/IQ1/IQ2).
// String-literal union (not an enum) — matches the backend's emitted enum-name values one-to-one.
export type GgufQuantTier = "NearLossless" | "SweetSpot" | "Balanced" | "Small" | "Minimal";

// Per-file hardware fit verdict the backend derives from file size vs free VRAM: Fits (size + margin ≤ free),
// Tight (fits but margin eats in), WontFit (size > free), Unknown (VRAM probe unavailable, e.g. no GPU / WSL).
export type GgufFitVerdict = "Fits" | "Tight" | "WontFit" | "Unknown";

// Domain view-model for one selectable .gguf file inside a repo (the quant picker rows). isDynamic flags an Unsloth
// "Dynamic" (UD-) quant so the UI can badge it; sizeBytes drives the size column. fileName is the exact file the
// download requests verbatim (so a chosen quant resolves unambiguously, including UD- quants). qualityTier/fitVerdict
// drive the per-row guidance badges; isRecommended marks the single ★ row the backend recommends (≤1 per non-empty list).
export interface GgufRepositoryFile {
	readonly fileName: string;
	readonly quant: string;
	readonly isDynamic: boolean;
	readonly sizeBytes: number;
	readonly qualityTier: GgufQuantTier;
	readonly fitVerdict: GgufFitVerdict;
	readonly isRecommended: boolean;
}

// Domain view-model for one repo's inspected detail: its selectable GGUF files (quants) keyed by repo id.
export interface GgufRepositoryDetail {
	readonly repoId: string;
	readonly files: readonly GgufRepositoryFile[];
}

// Pure rule for the picker's initial/derived selection: the backend-flagged recommended file when present, else the
// first listed file (legacy smallest-first order), else null for an empty list. Kept side-effect-free so the dialog
// can derive the effective selection without a derived-state effect, and so the rule is unit-testable in isolation.
export function recommendedGgufFileName(files: readonly GgufRepositoryFile[]): string | null {
	return (files.find((file) => file.isRecommended) ?? files[0])?.fileName ?? null;
}

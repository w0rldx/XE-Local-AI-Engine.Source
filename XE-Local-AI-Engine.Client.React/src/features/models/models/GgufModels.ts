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
}

// Domain view-model for one selectable .gguf file inside a repo (the quant picker rows). isDynamic flags an Unsloth
// "Dynamic" (UD-) quant so the UI can badge it; sizeBytes drives the size column. fileName is the exact file the
// download requests verbatim (so a chosen quant resolves unambiguously, including UD- quants).
export interface GgufRepositoryFile {
	readonly fileName: string;
	readonly quant: string;
	readonly isDynamic: boolean;
	readonly sizeBytes: number;
}

// Domain view-model for one repo's inspected detail: its selectable GGUF files (quants) keyed by repo id.
export interface GgufRepositoryDetail {
	readonly repoId: string;
	readonly files: readonly GgufRepositoryFile[];
}

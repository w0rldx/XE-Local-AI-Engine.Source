// Domain models for GGUF acquisition; GgufMappers normalizes optional generated wire fields.

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

// What opens the quant picker: a repo, plus the tested quant to preselect when the pick came from a tested catalog row.
export interface GgufDownloadTarget {
	readonly repoId: string;
	readonly preferredQuant?: string;
}

// Domain view-model for one curated-catalog model the authors ran through their live scenario checks, offered on the
// browse panel before any search. ggufRepo is the repo the download dialog inspects; fitVerdict grades testedQuant only.
export interface GgufTestedModel {
	readonly id: string;
	readonly displayName: string;
	readonly ggufRepo: string;
	readonly license: string;
	readonly totalParamsB: number;
	readonly notes: string | null;
	readonly testedQuant: string;
	readonly testedSizeBytes: number;
	readonly fitVerdict: GgufFitVerdict;
}

// Static quality tier the backend classifier assigns to a quant (no hardware involved). Ordered best→smallest:
// NearLossless (Q8_0/Q6_K/F16…), SweetSpot (Q5_K_*), Balanced (Q4_K_*), Small (Q3*/IQ3/IQ4/legacy), Minimal (Q2*/IQ1/IQ2).
// String-literal union (not an enum) — matches the backend's emitted enum-name values one-to-one.
export type GgufQuantTier = "NearLossless" | "SweetSpot" | "Balanced" | "Small" | "Minimal";

// Per-file hardware fit verdict the backend derives from file size vs free VRAM: Fits (size + margin ≤ free),
// Tight (fits but margin eats in), WontFit (size > free), Unknown (VRAM probe unavailable, e.g. no GPU / WSL).
export type GgufFitVerdict = "Fits" | "Tight" | "WontFit" | "Unknown";

// Mantine badge color per hardware fit verdict; Unknown is intentionally null (no probe → render a dimmed placeholder,
// not a misleading colored badge). Keys are exhaustive over GgufFitVerdict so a new verdict forces a compile update.
export const fitVerdictColor: Record<GgufFitVerdict, string | null> = {
	Fits: "green",
	Tight: "yellow",
	WontFit: "red",
	Unknown: null,
};

// i18n suffix under `pages.models.gguf.download.fit.*` per verdict (Unknown has no label — the download dialog shows a dimmed
// dash, the tested list shows no badge).
export const fitVerdictLabelKey: Record<Exclude<GgufFitVerdict, "Unknown">, string> = {
	Fits: "fits",
	Tight: "tight",
	WontFit: "wontFit",
};

const testedFitGroupRank: Record<GgufFitVerdict, number> = { Fits: 0, Tight: 1, WontFit: 2, Unknown: 3 };

// Best fitting first: Fits then Tight, each largest model first, then WontFit smallest first; ties by id (ordinal). While
// any verdict is Unknown the server order is kept, so a first landing reorders once, when the verdicts arrive.
export function sortTestedModelsByFit(models: readonly GgufTestedModel[]): readonly GgufTestedModel[] {
	if (models.some((model) => model.fitVerdict === "Unknown")) {
		return models;
	}
	return [...models].sort((a, b) => {
		const group = testedFitGroupRank[a.fitVerdict] - testedFitGroupRank[b.fitVerdict];
		if (group !== 0) {
			return group;
		}
		const size = a.fitVerdict === "WontFit" ? a.totalParamsB - b.totalParamsB : b.totalParamsB - a.totalParamsB;
		if (size !== 0) {
			return size;
		}
		return a.id < b.id ? -1 : a.id > b.id ? 1 : 0;
	});
}

// Domain view-model for one selectable .gguf file inside a repo (the quant picker rows). isDynamic flags an Unsloth
// "Dynamic" (UD-) quant so the UI can badge it; sizeBytes drives the size column. fileName is the exact file the
// download requests verbatim (so a chosen quant resolves unambiguously, including UD- quants). qualityTier/fitVerdict
// drive the per-row guidance badges; isRecommended marks the single ★ row the backend recommends (≤1 per non-empty list).
export interface GgufRepositoryFile {
	readonly fileName: string;
	readonly quant: string;
	readonly isDynamic: boolean;
	// True for a speculative-decoding DRAFT model (an MTP drafter) shipped alongside the real weights, not a base-model
	// quant. Its quant carries the backend's `MTP-` marker so it can never share a row label with the real file, and the
	// picker lists drafts in their own group with no quality grade — a drafter is not a usable chat model.
	readonly isDraft: boolean;
	readonly sizeBytes: number;
	readonly qualityTier: GgufQuantTier;
	readonly fitVerdict: GgufFitVerdict;
	readonly isRecommended: boolean;
}

// Domain view-model for one repo's inspected detail: its selectable GGUF files (quants) keyed by repo id.
export interface GgufRepositoryDetail {
	readonly repoId: string;
	readonly files: readonly GgufRepositoryFile[];
	// True when the repo ships an `mmproj*.gguf` vision projector beside the weights. Installing it makes the model
	// multimodal — and a model installed WITH a projector is refused as a benchmark judge, which is why the download
	// dialog offers a weights-only install for exactly these repos.
	readonly hasProjector: boolean;
	// Size of that projector, or null when the repo ships none (or an older backend does not report it).
	readonly projectorSizeBytes: number | null;
}

// Pure rule for the picker's initial/derived selection: the backend-flagged recommended file when present, else the
// first listed BASE quant (legacy smallest-first order), else null. A speculative-decoding draft is never the default
// selection — it is a companion, not a chat model — so a draft is only ever selected by an explicit operator click.
// Kept side-effect-free so the dialog can derive the effective selection without a derived-state effect, and so the
// rule is unit-testable in isolation.
export function recommendedGgufFileName(files: readonly GgufRepositoryFile[]): string | null {
	const baseQuants = files.filter((file) => !file.isDraft);
	return (files.find((file) => file.isRecommended) ?? baseQuants[0])?.fileName ?? null;
}

// The picker's default when opened from a tested catalog row: the file of the tested quant (case-insensitive) unless it
// is missing or won't fit, in which case the recommended file above stays the default.
export function preferredGgufFileName(files: readonly GgufRepositoryFile[], preferredQuant: string | undefined): string | null {
	const preferred =
		preferredQuant === undefined
			? undefined
			: files.find((file) => file.quant.toLowerCase() === preferredQuant.toLowerCase() && file.fitVerdict !== "WontFit");
	return preferred?.fileName ?? recommendedGgufFileName(files);
}

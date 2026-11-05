import { type KvCacheType, kvCacheTypes } from "@/core/models/KvCacheTypes";

// Speculative-decoding modes, each mapped to its capability class — mirrors the backend SpeculativeModeClass, and the
// `draft-` name prefix is NOT the capability test: `external-draft` runs a second GGUF drafter (needs a draft model,
// uses additional VRAM), `main-model-heads` (draft-mtp) drafts from multi-token-prediction heads inside the MAIN model
// and needs no draft model at all, `draftless` (ngram-*) self-speculates from context. Kept in sync with the backend
// accepted set (pinned llama.cpp build b12014). The full set is used for validation; the settings UI offers a curated
// subset (see speculativeModeSelectValues).
export const SPECULATIVE_DISABLED_MODE = "none";

type SpeculativeModeClass = "disabled" | "draftless" | "external-draft" | "main-model-heads";

const speculativeModeClasses = new Map<string, SpeculativeModeClass>([
	[SPECULATIVE_DISABLED_MODE, "disabled"],
	["ngram-simple", "draftless"],
	["ngram-map-k", "draftless"],
	["ngram-map-k4v", "draftless"],
	["ngram-mod", "draftless"],
	["ngram-cache", "draftless"],
	["draft-simple", "external-draft"],
	["draft-eagle3", "external-draft"],
	["draft-dflash", "external-draft"],
	["draft-dspark", "external-draft"],
	["draft-mtp", "main-model-heads"],
]);

// The modes surfaced in the settings Select (curated for operators), in display order. `none` renders as "Off".
export const speculativeModeSelectValues = [
	SPECULATIVE_DISABLED_MODE,
	"ngram-mod",
	"ngram-cache",
	"draft-simple",
	"draft-eagle3",
	"draft-dflash",
	"draft-dspark",
	"draft-mtp",
] as const;

// The provider default for a GPU chat spawn; the node setting seeds the launch policy with it when unset.
export const KV_CACHE_TYPE_DEFAULT: KvCacheType = "q8_0";

// The shared allow-list is the only one — see core/models/KvCacheTypes.
export const kvCacheTypeSelectValues = kvCacheTypes;

export function isAllowedKvCacheType(type: string): boolean {
	return (kvCacheTypeSelectValues as readonly string[]).includes(type.trim());
}

export function isAllowedSpeculativeMode(mode: string): boolean {
	return speculativeModeClasses.has(mode.trim());
}

// True only for modes that load a SECOND GGUF as the drafter, so the form must require a draft model for them.
// draft-mtp is deliberately false: its drafter lives in the main model.
export function requiresExternalDraftModel(mode: string): boolean {
	return speculativeModeClasses.get(mode.trim()) === "external-draft";
}

// `--spec-draft-n-max` (draft tokens per step) is honored by both draft classes, including draft-mtp; ngram-* modes
// size their drafts from their own knobs instead.
export function usesDraftTokensPerStep(mode: string): boolean {
	const modeClass = speculativeModeClasses.get(mode.trim());
	return modeClass === "external-draft" || modeClass === "main-model-heads";
}

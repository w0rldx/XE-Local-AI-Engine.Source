import type { i18n as I18n } from "i18next";

export type ExplanationLevel = "overview" | "detail" | "math";

// What a shown explanation rests on. The badge states it, so text about inferred structure never reads as read from the file.
export type ExplanationBasis = "background" | "measurement" | { inferredFrom: string };

// Wraps instead of clipping: the pinned llama.cpp tag at the end of an inferred badge is the part that matters.
export const inferredBadgeStyles = {
	root: { height: "auto" },
	label: { whiteSpace: "normal", overflow: "visible", overflowWrap: "anywhere" },
} as const;

// The trace summary's values, in the order the summary names them.
export const traceMetrics = ["meanNorm", "maxP99", "topDimensions"] as const;

// The first candidate with text at this level, in the page's language or its fallback; null when none has any, so the
// caller shows nothing instead of a raw key.
export function explanationKey(i18n: I18n, candidates: readonly string[], level: ExplanationLevel): string | null {
	return candidates.map((candidate) => `pages.modelLab.explain.${candidate}.${level}`).find((key) => i18n.exists(key)) ?? null;
}

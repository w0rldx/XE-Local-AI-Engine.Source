import type { i18n as I18n } from "i18next";

export type ExplanationLevel = "overview" | "detail" | "math";

// What a shown explanation rests on. The badge states it, so text about inferred structure never reads as read from the file.
// Unavailable: what the shown run or block cannot provide, so a missing view never reads as an empty measurement.
// Derived: computed from measured states, but not something the model computed, such as the logit lens.
export type ExplanationBasis =
	| "background"
	| "measurement"
	| "llamaCpp"
	| "simulated"
	| "unavailable"
	| "derived"
	| { inferredFrom: string };

const explanationRoot = "pages.modelLab.explain";

// Wraps instead of clipping: the pinned llama.cpp tag at the end of an inferred badge is the part that matters.
export const inferredBadgeStyles = {
	root: { height: "auto" },
	label: { whiteSpace: "normal", overflow: "visible", overflowWrap: "anywhere" },
} as const;

// The trace summary's values, in the order the summary names them.
export const traceMetrics = ["meanNorm", "maxP99", "topDimensions"] as const;

// The Next token tab's topics under pages.modelLab.nextToken.explain, each with the basis its badge states: what llama.cpp
// measured, what the sliders simulate, or general background.
export const nextTokenTopics = [
	{ id: "promptTokens", basis: "llamaCpp" },
	{ id: "logits", basis: "background" },
	{ id: "softmax", basis: "background" },
	{ id: "logprob", basis: "llamaCpp" },
	{ id: "temperature", basis: "simulated" },
	{ id: "topK", basis: "simulated" },
	{ id: "topP", basis: "simulated" },
	{ id: "minP", basis: "simulated" },
	{ id: "greedy", basis: "background" },
	// Background: its math is about the simulation, not a measured value.
	{ id: "entries", basis: "background" },
] as const satisfies readonly { id: string; basis: ExplanationBasis }[];

// The logit lens topics under pages.modelLab.explain.stepper, in the order the stepper's lens panel lists them.
export const lensTopics = ["lens", "lensChosen", "lensEntropy", "lensLastBlock", "lensCaveats", "lensNotRecorded"] as const;

// The routing topics under pages.modelLab.explain.stepper, in the order the stepper's routing panel lists them.
export const routingTopics = ["routing", "routingWeights", "routingUsage", "routingCaveats", "routingNotRecorded"] as const;

// A GGUF architecture that shares another's templates apart from some nodes: its own texts first, then the other's. Every
// text of the other must therefore hold for it too.
const architectureFallbacks = new Map([["qwen35moe", "qwen35"]]);

// A template node's explanation keys: per block kind, where one id means different operations in different templates, then
// per GGUF architecture (and its fallback), then shared. A kind text outranks every architecture, so it must hold for each
// family with that kind.
export function operationCandidates(kind: string, architecture: string | null, id: string): string[] {
	const fallback = architecture === null ? undefined : architectureFallbacks.get(architecture);
	const architectures = [architecture, fallback].filter((name) => typeof name === "string");
	return [`opByKind.${kind}.${id}`, ...architectures.map((name) => `arch.${name}.op.${id}`), `op.${id}`];
}

// The first candidate with text at this level, in the page's language or its fallback; null when none has any, so the
// caller shows nothing instead of a raw key.
export function explanationKey(
	i18n: I18n,
	candidates: readonly string[],
	level: ExplanationLevel,
	root = explanationRoot,
): string | null {
	return candidates.map((candidate) => `${root}.${candidate}.${level}`).find((key) => i18n.exists(key)) ?? null;
}

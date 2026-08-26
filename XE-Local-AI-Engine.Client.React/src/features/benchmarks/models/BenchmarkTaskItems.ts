import type { XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkTaskItemResponse as TaskItemResponse } from "@/core/api/generated";
import type { BenchmarkVerifierConfig } from "@/features/benchmarks/models/BenchmarkVerifier";

// A benchmark project holds 1..N task items. One item is the degenerate case and means exactly what a project meant
// before suites existed, so nothing here may change how a single-item project reads.

/**
 * `prompt` is an authored question. `niah` is a GENERATOR — never a run target — that expands at write time into
 * `niahCase` children, one per context length x needle depth. A case is an ordinary item with its own identity, which
 * is why every cap, every hash and every exclusion below applies to it for free.
 */
export const benchmarkTaskItemKinds = ["prompt", "niah", "niahCase"] as const;
export type BenchmarkTaskItemKind = (typeof benchmarkTaskItemKinds)[number];

/** An unrecognized kind reads as `prompt` — the node's own default for an item written before the generator kinds. */
export const toBenchmarkTaskItemKind = (value: unknown): BenchmarkTaskItemKind =>
	benchmarkTaskItemKinds.find((kind) => kind === value) ?? "prompt";

/**
 * The node's caps, mirrored so the editor refuses what the node would rather than after a round trip.
 * `maxLeafItems` is `BenchmarkTaskItemService.MaxTaskItems` and counts LEAVES, so a 2x3 NIAH config costs 6.
 * `maxRunsPerRequest` is `BenchmarkRunFreezeService.MaxRunsPerRequest` and bounds one freeze, not one cell.
 */
export const benchmarkTaskItemLimits = { maxLeafItems: 20, maxRunsPerRequest: 100 } as const;

export interface BenchmarkTaskItem {
	id: string;
	projectId: string;
	/** The generator this case was expanded from, or null for an authored item. */
	parentItemId: string | null;
	/** Display position. Not a scoring input, and deliberately not part of the project's item-set hash. */
	index: number;
	kind: BenchmarkTaskItemKind;
	/** Bumped on every edit of this item. Part of {@link BenchmarkTaskItem.inputHash}. */
	revision: number;
	/**
	 * What this item asks, as a value. Every run of it carries a copy taken at freeze; a run whose copy no longer
	 * matches answered a question that no longer exists and is excluded as `item-revised`.
	 */
	inputHash: string;
	/** Whether a freeze fans out over this item, or it only generates the items a freeze does. */
	isLeaf: boolean;
	countsTowardScore: boolean;
	prompt: string;
	referenceAnswer: string | null;
	/** Per-criterion overrides of the judge policy's verifier config, keyed by criterion id. */
	verifierConfig: Record<string, BenchmarkVerifierConfig> | null;
	/** Generator parameters — NIAH's context lengths and depths. Null for a plain prompt and for a materialized case. */
	generatorConfig: BenchmarkVerifierConfig | null;
	version: number;
	createdAtUtc: number;
	updatedAtUtc: number;
}

const configObject = (value: unknown): BenchmarkVerifierConfig | null =>
	typeof value === "object" && value !== null && !Array.isArray(value) ? (value as BenchmarkVerifierConfig) : null;

/** The per-criterion override map, dropping any entry that is not itself an object the verifier editor can open. */
function verifierOverrides(value: unknown): Record<string, BenchmarkVerifierConfig> | null {
	const parsed = configObject(value);
	if (parsed === null) {
		return null;
	}
	const entries = Object.entries(parsed)
		.map(([criterionId, config]) => [criterionId, configObject(config)] as const)
		.filter((entry): entry is readonly [string, BenchmarkVerifierConfig] => entry[1] !== null);
	return entries.length === 0 ? null : Object.fromEntries(entries);
}

export function toBenchmarkTaskItem(value: TaskItemResponse): BenchmarkTaskItem {
	return {
		id: value.id ?? "",
		projectId: value.projectId ?? "",
		parentItemId: value.parentItemId ?? null,
		index: value.index ?? 0,
		kind: toBenchmarkTaskItemKind(value.kind),
		revision: value.revision ?? 1,
		inputHash: value.inputHash,
		isLeaf: value.isLeaf ?? true,
		countsTowardScore: value.countsTowardScore ?? true,
		prompt: value.prompt,
		referenceAnswer: value.referenceAnswer ?? null,
		verifierConfig: verifierOverrides(value.verifierConfig),
		generatorConfig: configObject(value.generatorConfig),
		version: value.version ?? 0,
		createdAtUtc: value.createdAtUtc ?? 0,
		updatedAtUtc: value.updatedAtUtc ?? 0,
	};
}

/** What a freeze fans out over. A generator is not a run target, so it is never a leaf. */
export const leafBenchmarkTaskItems = (items: readonly BenchmarkTaskItem[]): BenchmarkTaskItem[] =>
	items.filter((item) => item.isLeaf);

/** The leaves whose scores enter the cell mean. A NIAH case is reported on its own axis and does not. */
export const scorableBenchmarkTaskItems = (items: readonly BenchmarkTaskItem[]): BenchmarkTaskItem[] =>
	items.filter((item) => item.isLeaf && item.countsTowardScore);

/** The cases one generator expanded into, in index order. Empty for an authored item. */
export const benchmarkTaskItemChildren = (items: readonly BenchmarkTaskItem[], parentId: string): BenchmarkTaskItem[] =>
	items.filter((item) => item.parentItemId === parentId).sort((left, right) => left.index - right.index);

/**
 * A NIAH generator's parameters, as plan §7.6 specifies them. Held loosely on purpose: the node owns the schema, and
 * an item written by a newer node must still open in this editor rather than lose members it does not know.
 */
export interface BenchmarkNiahGeneratorConfig {
	contextTokens: number[];
	needleDepthPercent: number[];
	needleTemplate: string;
	questionTemplate: string;
	seed: number | null;
}

export const defaultNiahGeneratorConfig: BenchmarkNiahGeneratorConfig = {
	contextTokens: [8192],
	needleDepthPercent: [10, 50, 90],
	needleTemplate: "The secret passcode for {city} is {code}.",
	questionTemplate: "What is the secret passcode for {city}?",
	seed: 0,
};

const numberList = (value: unknown): number[] =>
	Array.isArray(value) ? value.filter((entry): entry is number => typeof entry === "number" && Number.isFinite(entry)) : [];
const stringOr = (value: unknown, fallback: string): string => (typeof value === "string" ? value : fallback);

export function parseNiahGeneratorConfig(config: BenchmarkVerifierConfig | null): BenchmarkNiahGeneratorConfig {
	if (config === null) {
		return defaultNiahGeneratorConfig;
	}
	return {
		contextTokens: numberList(config["contextTokens"]),
		needleDepthPercent: numberList(config["needleDepthPercent"]),
		needleTemplate: stringOr(config["needleTemplate"], defaultNiahGeneratorConfig.needleTemplate),
		questionTemplate: stringOr(config["questionTemplate"], defaultNiahGeneratorConfig.questionTemplate),
		seed: typeof config["seed"] === "number" ? config["seed"] : null,
	};
}

/**
 * How many LEAF items this generator becomes — one per (context length x depth) pair. The operator sees this before
 * saving because six cases is six runs per cell and six against the item cap, not one.
 */
export const niahCaseCount = (config: BenchmarkNiahGeneratorConfig): number =>
	config.contextTokens.length * config.needleDepthPercent.length;

/** Why the node would refuse this generator, as an i18n key suffix, or null. */
export type BenchmarkNiahIssue = "contextTokensRequired" | "depthsRequired" | "depthRange" | "contextTooLarge" | "caseCap";

/**
 * The node refuses a probe longer than the project's frozen window at expansion, naming both numbers — a NIAH case
 * silently truncated to the window measures nothing. It is re-checked here so the operator learns it while editing.
 */
export function niahGeneratorIssue(
	config: BenchmarkNiahGeneratorConfig,
	projectContextTokens: number,
	existingLeafCount: number,
): BenchmarkNiahIssue | null {
	if (config.contextTokens.length === 0) {
		return "contextTokensRequired";
	}
	if (config.needleDepthPercent.length === 0) {
		return "depthsRequired";
	}
	if (config.needleDepthPercent.some((depth) => depth < 0 || depth > 100)) {
		return "depthRange";
	}
	if (config.contextTokens.some((tokens) => tokens > projectContextTokens)) {
		return "contextTooLarge";
	}
	return existingLeafCount + niahCaseCount(config) > benchmarkTaskItemLimits.maxLeafItems ? "caseCap" : null;
}

/** What the item form edits. The index, the revision and the input hash are the node's — a client may not name them. */
export interface BenchmarkTaskItemDraft {
	kind: BenchmarkTaskItemKind;
	prompt: string;
	referenceAnswer: string | null;
	verifierConfig: Record<string, BenchmarkVerifierConfig> | null;
	generatorConfig: BenchmarkVerifierConfig | null;
	countsTowardScore: boolean;
}

/**
 * A NIAH case is off the ranked mean by default (§15): recall at 32k is a different measurement from answer quality,
 * and averaging the two produces a number that is neither.
 */
export const emptyBenchmarkTaskItemDraft = (kind: BenchmarkTaskItemKind = "prompt"): BenchmarkTaskItemDraft => ({
	kind,
	prompt: "",
	referenceAnswer: null,
	verifierConfig: null,
	generatorConfig: kind === "niah" ? { ...defaultNiahGeneratorConfig } : null,
	countsTowardScore: kind !== "niah",
});

export const toBenchmarkTaskItemDraft = (item: BenchmarkTaskItem): BenchmarkTaskItemDraft => ({
	kind: item.kind,
	prompt: item.prompt,
	referenceAnswer: item.referenceAnswer,
	verifierConfig: item.verifierConfig,
	generatorConfig: item.generatorConfig,
	countsTowardScore: item.countsTowardScore,
});

/** Empty is not the same as absent: an empty override map is "no overrides", which the node reads as a null blob. */
export const pruneVerifierOverrides = (
	overrides: Record<string, BenchmarkVerifierConfig> | null,
): Record<string, BenchmarkVerifierConfig> | null => {
	if (overrides === null) {
		return null;
	}
	const kept = Object.entries(overrides).filter(([, config]) => Object.keys(config).length > 0);
	return kept.length === 0 ? null : Object.fromEntries(kept);
};

/** The move an operator just made, as a whole new order. Naming every current id IS the node's concurrency check. */
export function reorderBenchmarkTaskItems(items: readonly BenchmarkTaskItem[], itemId: string, direction: -1 | 1): string[] {
	const ids = items.map((item) => item.id);
	const from = ids.indexOf(itemId);
	const to = from + direction;
	if (from < 0 || to < 0 || to >= ids.length) {
		return ids;
	}
	const moved = [...ids];
	[moved[from], moved[to]] = [moved[to] as string, moved[from] as string];
	return moved;
}

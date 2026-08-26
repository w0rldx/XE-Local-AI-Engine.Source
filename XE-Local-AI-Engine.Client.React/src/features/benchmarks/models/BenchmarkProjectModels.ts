import type { XeLocalAiEngineProvidersAbstractionsContractsLocalModelOrigin } from "@/core/api/generated";
import type { BenchmarkProjectFidelity } from "@/features/benchmarks/models/BenchmarkFidelityModels";

export type BenchmarkOrigin = XeLocalAiEngineProvidersAbstractionsContractsLocalModelOrigin | null;

/** Mirrors the node's `BenchmarkJudgePolicyVersions` so the editor refuses what the server would reject anyway. */
export const benchmarkRubricLimits = {
	version: 1,
	minCriteria: 1,
	maxCriteria: 8,
	minWeight: 1,
	maxWeight: 100,
	maxIdLength: 32,
	maxTitleLength: 64,
	maxDescriptionLength: 1024,
	maxReferenceAnswerLength: 32768,
} as const;

export interface BenchmarkRubricCriterion {
	id: string;
	title: string;
	description: string;
	weight: number;
	/**
	 * How this criterion is decided: null (or absent) is the LLM judge reading the rubric, and a named kind is a
	 * deterministic verifier the node runs with no model in the loop. Carried through the editor UNREAD: the editor
	 * does not offer the choice yet, and dropping the member on a round-trip would silently turn a verifiable
	 * criterion back into a judged one — the operator's project quietly changing meaning because a form re-saved it.
	 *
	 * `BenchmarkVerifierEditor` is the UI boundary that makes these members editable; this model must preserve them
	 * whenever another project field is changed.
	 */
	kind?: string | null;
	/** The verifier's configuration, verbatim JSON from the node. Opaque here for the same reason `kind` is. */
	config?: string | null;
}

export interface BenchmarkRubric {
	version: number;
	criteria: BenchmarkRubricCriterion[];
}

/** Server-side criterion ids are `[a-z0-9-_]{1,32}`; the editor derives one from the title until the operator edits it. */
export function toBenchmarkCriterionId(title: string): string {
	return title
		.toLowerCase()
		.replace(/[^a-z0-9\-_]+/g, "-")
		.replace(/-{2,}/g, "-")
		.replace(/^-+|-+$/g, "")
		.slice(0, benchmarkRubricLimits.maxIdLength);
}

/**
 * How the judge decides. `pointwise` scores each run against the rubric on its own and stays the default;
 * `pairwise` compares runs against each other and ranks them through a Bradley-Terry fit of the verdicts.
 */
export const benchmarkJudgeModes = ["pointwise", "pairwise"] as const;
export type BenchmarkJudgeMode = (typeof benchmarkJudgeModes)[number];
/** An absent mode is the node's own default, which is pointwise. */
export const toBenchmarkJudgeMode = (value: unknown): BenchmarkJudgeMode =>
	benchmarkJudgeModes.find((mode) => mode === value) ?? "pointwise";

/** The project's current judge policy revision, or a disabled judge. Read-only: it is edited through a draft. */
export interface BenchmarkJudgePolicy {
	enabled: boolean;
	mode: BenchmarkJudgeMode;
	policyRevision: number | null;
	policyHash: string | null;
	modelName: string | null;
	requestedContextTokens: number | null;
	rubric: BenchmarkRubric | null;
	referenceAnswer: string | null;
	cohortGeneration: number | null;
	referenceExecutionKey: string | null;
	/**
	 * True when the stored revision carries a judge prompt version this build no longer judges under. The project
	 * still reads and existing scores stay ranked; new judgings refuse until the operator re-saves the judge.
	 */
	promptVersionOutdated: boolean;
}

export interface BenchmarkProjectSummary {
	id: string;
	name: string;
	contextTokens: number;
	/** Per-run output-token budget (`n_predict`), or null when generation is only limited by the context window. */
	maxOutputTokens: number | null;
	/** Per-run thinking budget, or null for "as much as the window allows". Additive with the output budget. */
	reasoningBudgetTokens: number | null;
	/** Seconds one run's generation may take before the node cancels it, or null for the node default (900). */
	invocationTimeoutSeconds: number | null;
	agentDefinitionId: string;
	judgeEnabled: boolean;
	runCount: number;
	isFrozen: boolean;
	version: number;
	createdAtUtc: number;
	updatedAtUtc: number;
}

export interface BenchmarkProjectDetail extends BenchmarkProjectSummary {
	coreTask: string;
	judge: BenchmarkJudgePolicy;
	fidelity: BenchmarkProjectFidelity;
}

/** What the project form edits. A null `rubric` means "use the node's default rubric", never "no rubric". */
export interface BenchmarkProjectDraft {
	name: string;
	coreTask: string;
	contextTokens: number;
	maxOutputTokens: number | null;
	reasoningBudgetTokens: number | null;
	invocationTimeoutSeconds: number | null;
	agentDefinitionId: string;
	judgeEnabled: boolean;
	judgeMode: BenchmarkJudgeMode;
	judgeModelName: string | null;
	judgeContextTokens: number | null;
	rubric: BenchmarkRubric | null;
	referenceAnswer: string | null;
	fidelityEnabled: boolean;
	fidelityKldEnabled: boolean;
	/** Null = the node's default. Outside 50..655 the node answers 400. */
	fidelityChunks: number | null;
	/** A `modelName` from the eligible-models list. Required by the node whenever KLD is on. */
	fidelityKldBaseModelName: string | null;
}

/** The four settable fidelity members, shared by the create request and the fidelity PATCH. */
export interface BenchmarkProjectFidelityDraft {
	fidelityEnabled: boolean;
	fidelityKldEnabled: boolean;
	fidelityChunks: number | null;
	fidelityKldBaseModelName: string | null;
}

export interface BenchmarkEligibleModel {
	modelName: string;
	maxContextTokens: number | null;
	effectiveContextTokens: number | null;
	origin: BenchmarkOrigin;
	modelContentFingerprint: string;
	supportsTools: boolean;
}

/**
 * The first thing the node's `BenchmarkJudgePolicyValidator` would reject, mirrored client-side so the operator is not
 * told about a bad criterion by a round-trip. `index` names the offending criterion, or -1 for a rubric-level issue.
 */
export interface BenchmarkRubricIssue {
	code: "count" | "id" | "duplicateId" | "title" | "description" | "weight";
	index: number;
}

const criterionId = /^[a-z0-9\-_]+$/;

export function benchmarkRubricIssue(rubric: BenchmarkRubric): BenchmarkRubricIssue | null {
	const { criteria } = rubric;
	if (criteria.length < benchmarkRubricLimits.minCriteria || criteria.length > benchmarkRubricLimits.maxCriteria) {
		return { code: "count", index: -1 };
	}
	const seen = new Set<string>();
	for (const [index, criterion] of criteria.entries()) {
		if (criterion.id.length === 0 || criterion.id.length > benchmarkRubricLimits.maxIdLength || !criterionId.test(criterion.id)) {
			return { code: "id", index };
		}
		if (seen.has(criterion.id)) {
			return { code: "duplicateId", index };
		}
		seen.add(criterion.id);
		if (criterion.title.trim().length === 0 || criterion.title.length > benchmarkRubricLimits.maxTitleLength) {
			return { code: "title", index };
		}
		if (criterion.description.trim().length === 0 || criterion.description.length > benchmarkRubricLimits.maxDescriptionLength) {
			return { code: "description", index };
		}
		if (criterion.weight < benchmarkRubricLimits.minWeight || criterion.weight > benchmarkRubricLimits.maxWeight) {
			return { code: "weight", index };
		}
	}
	return null;
}

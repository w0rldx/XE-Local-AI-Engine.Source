import type {
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkJudgePolicyResponse as JudgePolicyResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkProjectDetailResponse as ProjectDetailResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkProjectSummaryResponse as ProjectSummaryResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRankCohortResponse as RankCohortResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRubricDto as RubricResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRunDetailResponse as RunDetailResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRunSummaryResponse as RunSummaryResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1EligibleBenchmarkModelResponse as EligibleModelResponse,
} from "@/core/api/generated";
import type {
	BenchmarkEligibleModel,
	BenchmarkEvidenceObject,
	BenchmarkFlashAttentionMode,
	BenchmarkJudgeCriterionScore,
	BenchmarkJudgePolicy,
	BenchmarkKvCacheTypeSource,
	BenchmarkLaunchFacts,
	BenchmarkOrigin,
	BenchmarkOutputPart,
	BenchmarkPrimaryStatus,
	BenchmarkProjectDetail,
	BenchmarkProjectSummary,
	BenchmarkRankCohort,
	BenchmarkRubric,
	BenchmarkRunDetail,
	BenchmarkRunJudge,
	BenchmarkRunSummary,
} from "@/features/benchmarks/models/BenchmarkModels";
import {
	benchmarkRubricLimits,
	toBenchmarkJudgeState,
	toBenchmarkQualityScoreSource,
	toBenchmarkRankExclusionReason,
} from "@/features/benchmarks/models/BenchmarkModels";

// The generated OpenAPI shapes intentionally keep most response members optional. These boundary mappers supply
// stable UI defaults while preserving all lifecycle/provenance distinctions the benchmark contract exposes.
function origin(value: unknown): BenchmarkOrigin {
	return value === "huggingface" || value === "imported" ? value : null;
}
const numberValue = (value: number | undefined, fallback = 0): number => value ?? fallback;

export function toBenchmarkProjectSummary(value: ProjectSummaryResponse): BenchmarkProjectSummary {
	return {
		id: value.id ?? "",
		name: value.name,
		contextTokens: numberValue(value.contextTokens),
		agentDefinitionId: value.agentDefinitionId ?? "",
		judgeEnabled: value.judgeEnabled === true,
		runCount: numberValue(value.runCount),
		isFrozen: value.isFrozen === true,
		version: numberValue(value.version),
		createdAtUtc: numberValue(value.createdAtUtc),
		updatedAtUtc: numberValue(value.updatedAtUtc),
	};
}

/** A rubric the node did not send is "use the default", which the editor renders from the presets — never an empty one. */
export function toBenchmarkRubric(value: RubricResponse | null | undefined): BenchmarkRubric | null {
	if (!value?.criteria) {
		return null;
	}
	return {
		version: value.version ?? benchmarkRubricLimits.version,
		criteria: value.criteria.map((criterion) => ({
			id: criterion.id ?? "",
			title: criterion.title ?? "",
			description: criterion.description ?? "",
			weight: criterion.weight ?? benchmarkRubricLimits.minWeight,
		})),
	};
}

function toBenchmarkJudgePolicy(value: JudgePolicyResponse | undefined): BenchmarkJudgePolicy {
	return {
		enabled: value?.enabled === true,
		policyRevision: value?.policyRevision ?? null,
		policyHash: value?.policyHash ?? null,
		modelName: value?.modelName ?? null,
		requestedContextTokens: value?.requestedContextTokens ?? null,
		rubric: toBenchmarkRubric(value?.rubric),
		referenceAnswer: value?.referenceAnswer ?? null,
		cohortGeneration: value?.cohortGeneration ?? null,
		referenceExecutionKey: value?.referenceExecutionKey ?? null,
	};
}

export function toBenchmarkProjectDetail(value: ProjectDetailResponse): BenchmarkProjectDetail {
	return {
		...toBenchmarkProjectSummary(value),
		coreTask: value.coreTask,
		judge: toBenchmarkJudgePolicy(value.judge),
	};
}

const primaryStatus = (value: RunSummaryResponse["primaryStatus"]): BenchmarkPrimaryStatus => value ?? "Failed";

function judgeCriteria(value: RunSummaryResponse["judge"]): BenchmarkJudgeCriterionScore[] {
	return (value?.criteria ?? []).map((criterion) => ({
		id: criterion.id,
		score: criterion.score ?? 0,
		rationale: criterion.rationale,
	}));
}

// `policyCurrent`/`executionCurrent` fail closed: an absent flag means "cannot be ranked", never "ranked by default".
function toBenchmarkRunJudge(value: RunSummaryResponse["judge"]): BenchmarkRunJudge {
	return {
		state: toBenchmarkJudgeState(value?.state),
		score: value?.score ?? null,
		policyRevision: value?.policyRevision ?? null,
		attemptSequence: value?.attemptSequence ?? null,
		cohortGeneration: value?.cohortGeneration ?? null,
		executionKey: value?.executionKey ?? null,
		policyCurrent: value?.policyCurrent === true,
		executionCurrent: value?.executionCurrent === true,
		errorMessage: value?.errorMessage ?? null,
		summary: value?.summary ?? null,
		criteria: judgeCriteria(value),
	};
}

export function toBenchmarkRankCohort(value: RankCohortResponse | undefined): BenchmarkRankCohort {
	return {
		policyRevision: value?.policyRevision ?? null,
		executionKey: value?.executionKey ?? null,
		cohortGeneration: value?.cohortGeneration ?? null,
		rankedCount: numberValue(value?.rankedCount),
		totalScored: numberValue(value?.totalScored),
	};
}

const text = (value: unknown): string | null => (typeof value === "string" && value.length > 0 ? value : null);
const count = (value: unknown): number | null => (typeof value === "number" && Number.isFinite(value) ? value : null);
const flag = (value: unknown): boolean | null => (typeof value === "boolean" ? value : null);
const kvSource = (value: unknown): BenchmarkKvCacheTypeSource | null => (value === "explicit" || value === "auto" ? value : null);
const flashAttention = (value: unknown): BenchmarkFlashAttentionMode | null =>
	value === "auto" || value === "on" ? value : null;
const evidenceObject = (value: unknown): BenchmarkEvidenceObject | null =>
	typeof value === "object" && value !== null && !Array.isArray(value) ? (value as BenchmarkEvidenceObject) : null;

// Every member is nullable by contract (D7: legacy rows predate the receipt and stay NULL).
function launchFacts(value: RunSummaryResponse): BenchmarkLaunchFacts {
	const at = (suffix: string): unknown => (value as Record<string, unknown>)[`primary${suffix}`];
	return {
		variant: text(at("Variant")),
		kvCacheType: text(at("KvCacheType")),
		kvCacheTypeSource: kvSource(at("KvCacheTypeSource")),
		kvAutoReason: text(at("KvAutoReason")),
		flashAttentionMode: flashAttention(at("FlashAttentionMode")),
		intendedLaunchIdentity: text(at("IntendedLaunchIdentity")),
		intendedExecutableSha256: text(at("IntendedExecutableSha256")),
		effectiveLaunchIdentity: text(at("EffectiveLaunchIdentity")),
		effectiveBackend: text(at("EffectiveBackend")),
		placementOffloaded: count(at("PlacementOffloaded")),
		placementTotal: count(at("PlacementTotal")),
		executableSha256: text(at("ExecutableSha256")),
		hasAuxAssets: flag(at("HasAuxAssets")),
		receiptHash: text(at("ReceiptHash")),
		environmentFactsHash: text(at("EnvironmentFactsHash")),
	};
}

export function toBenchmarkRunSummary(value: RunSummaryResponse): BenchmarkRunSummary {
	return {
		primaryLaunch: launchFacts(value),
		id: value.id ?? "",
		projectId: value.projectId ?? "",
		primaryModelName: value.primaryModelName,
		primaryModelOrigin: origin(value.primaryModelOrigin),
		modelContentFingerprint: value.modelContentFingerprint,
		modelGroupKey: value.modelGroupKey ?? value.modelContentFingerprint,
		agentName: value.agentName,
		agentVersion: numberValue(value.agentVersion),
		requestedContextTokens: numberValue(value.requestedContextTokens),
		primaryStatus: primaryStatus(value.primaryStatus),
		judge: toBenchmarkRunJudge(value.judge),
		qualityScore: value.qualityScore ?? null,
		qualityScoreSource: toBenchmarkQualityScoreSource(value.qualityScoreSource),
		rank: value.rank ?? null,
		rankExclusionReason: toBenchmarkRankExclusionReason(value.rankExclusionReason),
		effectiveContextTokens: value.effectiveContextTokens ?? null,
		durationMs: value.durationMs ?? null,
		totalTokens: value.totalTokens ?? null,
		tokensPerSecond: value.tokensPerSecond ?? null,
		userScore: value.userScore ?? null,
		lastStreamSequence: numberValue(value.lastStreamSequence),
		version: numberValue(value.version),
		createdAtUtc: numberValue(value.createdAtUtc),
		updatedAtUtc: numberValue(value.updatedAtUtc),
	};
}

function outputParts(value: unknown): BenchmarkOutputPart[] {
	if (!Array.isArray(value)) {
		return [];
	}
	return value.filter((part): part is BenchmarkOutputPart => typeof part === "object" && part !== null && "kind" in part);
}

export function toBenchmarkRunDetail(value: RunDetailResponse): BenchmarkRunDetail {
	return {
		...toBenchmarkRunSummary(value),
		primaryLaunchReceipt: evidenceObject(value.primaryLaunchReceipt),
		primaryEnvironmentFacts: evidenceObject(value.primaryEnvironmentFacts),
		outputParts: outputParts(value.outputParts),
		primaryErrorMessage: value.primaryErrorMessage ?? null,
		startedAtUtc: value.startedAtUtc ?? null,
		primaryCompletedAtUtc: value.primaryCompletedAtUtc ?? null,
	};
}

export const toBenchmarkEligibleModel = (value: EligibleModelResponse): BenchmarkEligibleModel => ({
	modelName: value.modelName,
	maxContextTokens: value.maxContextTokens ?? null,
	effectiveContextTokens: value.effectiveContextTokens ?? null,
	origin: origin(value.origin),
	modelContentFingerprint: value.modelContentFingerprint,
	supportsTools: value.supportsTools === true,
});

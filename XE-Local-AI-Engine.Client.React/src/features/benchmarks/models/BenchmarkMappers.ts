import type {
	XeLocalAiEngineClientEndpointsBenchmarksV1ListBenchmarkComparisonsResponse as ComparisonsResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1EligibleBenchmarkModelResponse as EligibleModelResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkFidelityResponse as FidelityResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkJudgePolicyResponse as JudgePolicyResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1GetBenchmarkPairwiseEstimateResponse as PairwiseEstimateResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkProjectDetailResponse as ProjectDetailResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkProjectSummaryResponse as ProjectSummaryResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRankCohortResponse as RankCohortResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRubricDto as RubricResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRunDetailResponse as RunDetailResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRunSummaryResponse as RunSummaryResponse,
} from "@/core/api/generated";
import type {
	BenchmarkComparisonList,
	BenchmarkEligibleModel,
	BenchmarkEvidenceObject,
	BenchmarkFlashAttentionMode,
	BenchmarkJudgeCriterionScore,
	BenchmarkJudgePolicy,
	BenchmarkKvCacheTypeSource,
	BenchmarkLaunchFacts,
	BenchmarkOrigin,
	BenchmarkOutputPart,
	BenchmarkPairwiseEstimate,
	BenchmarkPrimaryStatus,
	BenchmarkProjectDetail,
	BenchmarkProjectFidelity,
	BenchmarkProjectSummary,
	BenchmarkRankCohort,
	BenchmarkRubric,
	BenchmarkRunDetail,
	BenchmarkRunFidelity,
	BenchmarkRunJudge,
	BenchmarkRunSummary,
	BenchmarkRunVerifier,
} from "@/features/benchmarks/models/BenchmarkModels";
import {
	benchmarkFidelityChunkLimits,
	benchmarkRubricLimits,
	toBenchmarkFidelityKldState,
	toBenchmarkFidelityStatus,
	toBenchmarkJudgeMode,
	toBenchmarkJudgeState,
	toBenchmarkQualityScoreSource,
	toBenchmarkRankExclusionReason,
	toBenchmarkRepeatMode,
	toBenchmarkVerdict,
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
		maxOutputTokens: value.maxOutputTokens ?? null,
		reasoningBudgetTokens: value.reasoningBudgetTokens ?? null,
		invocationTimeoutSeconds: value.invocationTimeoutSeconds ?? null,
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
			// Preserved rather than defaulted: the editor sends the mapped rubric straight back, so a dropped `kind`
			// would re-save a deterministically verified criterion as an LLM-judged one and change the project's
			// meaning without the operator touching it.
			kind: criterion.kind ?? null,
			config: criterion.config ?? null,
		})),
	};
}

function toBenchmarkJudgePolicy(value: JudgePolicyResponse | undefined): BenchmarkJudgePolicy {
	return {
		enabled: value?.enabled === true,
		mode: toBenchmarkJudgeMode(value?.mode),
		policyRevision: value?.policyRevision ?? null,
		policyHash: value?.policyHash ?? null,
		modelName: value?.modelName ?? null,
		requestedContextTokens: value?.requestedContextTokens ?? null,
		rubric: toBenchmarkRubric(value?.rubric),
		referenceAnswer: value?.referenceAnswer ?? null,
		cohortGeneration: value?.cohortGeneration ?? null,
		referenceExecutionKey: value?.referenceExecutionKey ?? null,
		promptVersionOutdated: value?.promptVersionOutdated === true,
	};
}

// `chunksEffective` is what runs; `chunks` is what the operator typed. Keeping both is the difference between a form
// that shows "200" as if it had been chosen and one that shows the default is in force.
function toBenchmarkProjectFidelity(value: ProjectDetailResponse): BenchmarkProjectFidelity {
	return {
		enabled: value.fidelityEnabled === true,
		kldEnabled: value.fidelityKldEnabled === true,
		chunks: value.fidelityChunks ?? null,
		chunksEffective: numberValue(value.fidelityChunksEffective, benchmarkFidelityChunkLimits.default),
		kldBaseModelName: value.fidelityKldBaseModelName ?? null,
		kldBaseFingerprint: value.fidelityKldBaseFingerprint ?? null,
		kldExpectedDigest: value.fidelityKldExpectedDigest ?? null,
	};
}

export function toBenchmarkProjectDetail(value: ProjectDetailResponse): BenchmarkProjectDetail {
	return {
		...toBenchmarkProjectSummary(value),
		coreTask: value.coreTask,
		judge: toBenchmarkJudgePolicy(value.judge),
		fidelity: toBenchmarkProjectFidelity(value),
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

// The node sends evidence only for verifiable criteria, so an empty list is the ordinary case for an all-LLM rubric.
function judgeVerifiers(value: RunSummaryResponse["judge"]): BenchmarkRunVerifier[] {
	return (value?.verifiers ?? []).map((verifier) => ({
		id: verifier.id,
		kind: verifier.kind,
		// Fail closed: an absent flag reads as "did not pass", never as a pass the node never asserted.
		passed: verifier.passed === true,
		detail: verifier.detail,
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
		verifiers: judgeVerifiers(value),
		criteria: judgeCriteria(value),
	};
}

/**
 * A run's fidelity numbers. The KLD trio is read through {@link toBenchmarkFidelityKldState}: the node already withholds
 * the figures unless the run's stored base-logit digest is the one the project now expects, and the state is carried
 * through unchanged so the UI can say WHY a number is missing rather than showing a bare dash.
 */
export function toBenchmarkRunFidelity(value: FidelityResponse | null | undefined): BenchmarkRunFidelity | null {
	if (!value) {
		return null;
	}
	return {
		status: toBenchmarkFidelityStatus(value.status),
		attemptId: value.attemptId ?? null,
		perplexityMean: value.perplexityMean ?? null,
		perplexityStdErr: value.perplexityStdErr ?? null,
		perplexityChunks: value.perplexityChunks ?? null,
		perplexityContextTokens: value.perplexityContextTokens ?? null,
		perplexityCorpusId: value.perplexityCorpusId ?? null,
		kldState: toBenchmarkFidelityKldState(value.kldState),
		kldMean: value.kldMean ?? null,
		kldP99: value.kldP99 ?? null,
		topTokenAgreement: value.topTokenAgreement ?? null,
		kldBaseFingerprint: value.kldBaseFingerprint ?? null,
		errorMessage: value.errorMessage ?? null,
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

/**
 * The verdicts and the fit as ONE read. `isCurrent` is carried through untouched: a fit that no longer describes the
 * cohort is the `pairwise-stale` case, and the caller must be able to refuse the score rather than be handed a number
 * with no way to tell.
 */
export function toBenchmarkComparisonList(value: ComparisonsResponse): BenchmarkComparisonList {
	const fit = value.fit;
	return {
		cohortGeneration: numberValue(value.cohortGeneration),
		comparisonSetVersion: numberValue(value.comparisonSetVersion),
		referenceExecutionKey: value.referenceExecutionKey ?? null,
		items: (value.items ?? []).map((item) => ({
			id: item.id ?? "",
			runAId: item.runAId ?? "",
			runBId: item.runBId ?? "",
			order: numberValue(item.order),
			attemptSequence: numberValue(item.attemptSequence),
			sequence: numberValue(item.sequence),
			taskCaseId: item.taskCaseId ?? null,
			status: item.status,
			verdict: toBenchmarkVerdict(item.verdict),
			answerATruncated: item.answerATruncated === true,
			answerBTruncated: item.answerBTruncated === true,
			judgeExecutionKey: item.judgeExecutionKey ?? null,
			errorMessage: item.errorMessage ?? null,
			enqueuedAtUtc: numberValue(item.enqueuedAtUtc),
			completedAtUtc: item.completedAtUtc ?? null,
		})),
		fit:
			fit == null
				? null
				: {
						fitKey: fit.fitKey,
						judgeExecutionKey: fit.judgeExecutionKey,
						comparisonSetVersion: numberValue(fit.comparisonSetVersion),
						cohortGeneration: numberValue(fit.cohortGeneration),
						iterations: numberValue(fit.iterations),
						bootstrapReplicates: numberValue(fit.bootstrapReplicates),
						// Fail closed: an absent flag reads as "not current", so a score is withheld rather than shown stale.
						isCurrent: fit.isCurrent === true,
						createdAtUtc: numberValue(fit.createdAtUtc),
						scores: (fit.scores ?? []).map((score) => ({
							runId: score.runId ?? "",
							score: score.score ?? null,
							ciLow: score.ciLow ?? null,
							ciHigh: score.ciHigh ?? null,
							comparisons: numberValue(score.comparisons),
							bootstrapAppearances: numberValue(score.bootstrapAppearances),
							reason: score.reason ?? null,
						})),
					},
	};
}

export const toBenchmarkPairwiseEstimate = (value: PairwiseEstimateResponse): BenchmarkPairwiseEstimate => ({
	eligibleRuns: numberValue(value.eligibleRuns),
	pairedRuns: numberValue(value.pairedRuns),
	cappedRuns: numberValue(value.cappedRuns),
	judgeCalls: numberValue(value.judgeCalls),
	// Null stays null: the caller omits the ETA entirely rather than rendering "0 s", which would read as instant.
	estimatedSeconds: value.estimatedSeconds ?? null,
	warn: value.warn === true,
	maximumRuns: numberValue(value.maximumRuns),
});

const text = (value: unknown): string | null => (typeof value === "string" && value.length > 0 ? value : null);
const count = (value: unknown): number | null => (typeof value === "number" && Number.isFinite(value) ? value : null);
const flag = (value: unknown): boolean | null => (typeof value === "boolean" ? value : null);
const kvSource = (value: unknown): BenchmarkKvCacheTypeSource | null => (value === "explicit" || value === "auto" ? value : null);
const flashAttention = (value: unknown): BenchmarkFlashAttentionMode | null =>
	value === "auto" || value === "on" ? value : null;
const evidenceObject = (value: unknown): BenchmarkEvidenceObject | null =>
	typeof value === "object" && value !== null && !Array.isArray(value) ? (value as BenchmarkEvidenceObject) : null;

// Every member is nullable by contract because legacy rows predate the receipt and stay null.
function launchFacts(value: RunSummaryResponse): BenchmarkLaunchFacts {
	const at = (suffix: string): unknown => (value as Record<string, unknown>)[`primary${suffix}`];
	return {
		variant: text(at("Variant")),
		kvCacheType: text(at("KvCacheType")),
		kvCacheTypeSource: kvSource(at("KvCacheTypeSource")),
		kvAutoReason: text(at("KvAutoReason")),
		flashAttentionMode: flashAttention(at("FlashAttentionMode")),
		intendedLaunchIdentity: text(at("IntendedLaunchIdentity")),
		launchIdentitySchemeOutdated: flag(at("LaunchIdentitySchemeOutdated")),
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
		repeatGroupId: value.repeatGroupId ?? null,
		repeatIndex: value.repeatIndex ?? null,
		isWarmup: value.isWarmup === true,
		repeatMode: toBenchmarkRepeatMode(value.repeatMode),
		samplingSeed: value.samplingSeed ?? null,
		samplingTemperature: value.samplingTemperature ?? null,
		agentName: value.agentName,
		agentVersion: numberValue(value.agentVersion),
		requestedContextTokens: numberValue(value.requestedContextTokens),
		primaryStatus: primaryStatus(value.primaryStatus),
		judge: toBenchmarkRunJudge(value.judge),
		fidelity: toBenchmarkRunFidelity(value.fidelity),
		qualityScore: value.qualityScore ?? null,
		qualityScoreSource: toBenchmarkQualityScoreSource(value.qualityScoreSource),
		rank: value.rank ?? null,
		rankExclusionReason: toBenchmarkRankExclusionReason(value.rankExclusionReason),
		primaryStopReason: value.primaryStopReason ?? null,
		effectiveContextTokens: value.effectiveContextTokens ?? null,
		durationMs: value.durationMs ?? null,
		totalTokens: value.totalTokens ?? null,
		tokensPerSecond: value.tokensPerSecond ?? null,
		throughput: {
			ttftMs: value.ttftMs ?? null,
			promptTokens: value.promptTokens ?? null,
			promptTokensPerSecond: value.promptTokensPerSecond ?? null,
			generationTokens: value.generationTokens ?? null,
			generationTokensPerSecond: value.generationTokensPerSecond ?? null,
			cachedPromptTokens: value.cachedPromptTokens ?? null,
			segmentCount: value.segmentCount ?? null,
		},
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
		reasoningBudgetTokens: value.reasoningBudgetTokens ?? null,
		reasoningBudgetApplicable: value.reasoningBudgetApplicable ?? null,
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

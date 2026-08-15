import type {
	XeLocalAiEngineClientEndpointsTrainingComparisonsV1ComparisonResponse as ComparisonResponse,
	XeLocalAiEngineClientEndpointsTrainingComparisonsV1ComparisonSuggestionResponse as ComparisonSuggestionResponse,
	XeLocalAiEngineClientEndpointsTrainingEvaluationsV1EvaluationResponse as EvaluationResponse,
} from "@/core/api/generated";

// Domain types + wire→domain mappers for the evaluation/comparison surface. Same posture as TrainingModels: the
// mappers coalesce the generated all-optional DTO fields into required domain fields; they do NOT re-validate.

export type EvaluationStatus = "Queued" | "Running" | "Succeeded" | "Failed" | "Cancelled";

export interface EvaluationKindTally {
	kind: string;
	total: number;
	passed: number;
}

export interface EvaluationRun {
	id: string;
	trainingRunId: string | null;
	comparisonId: string | null;
	modelName: string;
	datasetId: string;
	/** The dataset fingerprint frozen when this evaluation took its hold-out set. */
	datasetContentFingerprint: string;
	status: EvaluationStatus;
	totalCount: number;
	scoredCount: number;
	passedCount: number;
	perKind: EvaluationKindTally[];
	errorMessage: string | null;
	version: number;
}

export interface ComparisonKindDelta {
	kind: string;
	baseTotal: number;
	basePassed: number;
	tunedTotal: number;
	tunedPassed: number;
	baseAccuracy: number;
	tunedAccuracy: number;
	accuracyDelta: number;
}

export interface ComparisonBenchmarkDelta {
	baseTokensPerSecond: number | null;
	tunedTokensPerSecond: number | null;
	tokensPerSecondDelta: number | null;
	baseDurationMs: number | null;
	tunedDurationMs: number | null;
	baseUserScore: number | null;
	tunedUserScore: number | null;
	userScoreDelta: number | null;
	baseJudgeScore: number | null;
	tunedJudgeScore: number | null;
	judgeScoreDelta: number | null;
}

export interface ComparisonDeltas {
	baseModelName: string;
	tunedModelName: string;
	baseScoredCount: number;
	basePassedCount: number;
	tunedScoredCount: number;
	tunedPassedCount: number;
	baseAccuracy: number;
	tunedAccuracy: number;
	accuracyDelta: number;
	perKind: ComparisonKindDelta[];
	accuracyAvailable: boolean;
	unavailableReason: string | null;
	benchmark: ComparisonBenchmarkDelta | null;
}

export interface ComparisonReport {
	id: string;
	name: string;
	baseEvaluationRunId: string;
	tunedEvaluationRunId: string;
	baseBenchmarkRunId: string | null;
	tunedBenchmarkRunId: string | null;
	trainingRunId: string | null;
	deltas: ComparisonDeltas | null;
	version: number;
	createdAtUtc: number;
}

export interface ComparisonSuggestion {
	trainingRunId: string;
	baseModelName: string | null;
	tunedModelName: string | null;
	baseEvaluationRunId: string | null;
	tunedEvaluationRunId: string | null;
	unavailableReason: string | null;
}

const evaluationStatuses: EvaluationStatus[] = ["Queued", "Running", "Succeeded", "Failed", "Cancelled"];

/** An unknown status string degrades to Queued rather than throwing — a wire value is not worth a blank page over. */
function toEvaluationStatus(value: string): EvaluationStatus {
	return evaluationStatuses.includes(value as EvaluationStatus) ? (value as EvaluationStatus) : "Queued";
}

export function toEvaluationRun(response: EvaluationResponse): EvaluationRun {
	return {
		id: response.id,
		trainingRunId: response.trainingRunId ?? null,
		comparisonId: response.comparisonId ?? null,
		modelName: response.modelName,
		datasetId: response.datasetId,
		datasetContentFingerprint: response.datasetContentFingerprint,
		status: toEvaluationStatus(response.status),
		totalCount: response.totalCount,
		scoredCount: response.scoredCount,
		passedCount: response.passedCount,
		perKind: (response.perKind ?? []).map((tally) => ({ kind: tally.kind, total: tally.total, passed: tally.passed })),
		errorMessage: response.errorMessage ?? null,
		version: response.version,
	};
}

export function toComparisonReport(response: ComparisonResponse): ComparisonReport {
	const deltas = response.deltas;
	return {
		id: response.id,
		name: response.name,
		baseEvaluationRunId: response.baseEvaluationRunId,
		tunedEvaluationRunId: response.tunedEvaluationRunId,
		baseBenchmarkRunId: response.baseBenchmarkRunId ?? null,
		tunedBenchmarkRunId: response.tunedBenchmarkRunId ?? null,
		trainingRunId: response.trainingRunId ?? null,
		deltas:
			deltas == null
				? null
				: {
						baseModelName: deltas.baseModelName,
						tunedModelName: deltas.tunedModelName,
						baseScoredCount: deltas.baseScoredCount,
						basePassedCount: deltas.basePassedCount,
						tunedScoredCount: deltas.tunedScoredCount,
						tunedPassedCount: deltas.tunedPassedCount,
						baseAccuracy: deltas.baseAccuracy,
						tunedAccuracy: deltas.tunedAccuracy,
						accuracyDelta: deltas.accuracyDelta,
						perKind: (deltas.perKind ?? []).map((kind) => ({ ...kind })),
						accuracyAvailable: deltas.accuracyAvailable,
						unavailableReason: deltas.unavailableReason ?? null,
						benchmark:
							deltas.benchmark == null
								? null
								: {
										baseTokensPerSecond: deltas.benchmark.baseTokensPerSecond ?? null,
										tunedTokensPerSecond: deltas.benchmark.tunedTokensPerSecond ?? null,
										tokensPerSecondDelta: deltas.benchmark.tokensPerSecondDelta ?? null,
										baseDurationMs: deltas.benchmark.baseDurationMs ?? null,
										tunedDurationMs: deltas.benchmark.tunedDurationMs ?? null,
										baseUserScore: deltas.benchmark.baseUserScore ?? null,
										tunedUserScore: deltas.benchmark.tunedUserScore ?? null,
										userScoreDelta: deltas.benchmark.userScoreDelta ?? null,
										baseJudgeScore: deltas.benchmark.baseJudgeScore ?? null,
										tunedJudgeScore: deltas.benchmark.tunedJudgeScore ?? null,
										judgeScoreDelta: deltas.benchmark.judgeScoreDelta ?? null,
									},
					},
		version: response.version,
		createdAtUtc: response.createdAtUtc,
	};
}

export function toComparisonSuggestion(response: ComparisonSuggestionResponse): ComparisonSuggestion {
	return {
		trainingRunId: response.trainingRunId,
		baseModelName: response.baseModelName ?? null,
		tunedModelName: response.tunedModelName ?? null,
		baseEvaluationRunId: response.baseEvaluationRunId ?? null,
		tunedEvaluationRunId: response.tunedEvaluationRunId ?? null,
		unavailableReason: response.unavailableReason ?? null,
	};
}

/** True while the evaluation is still on the queue or being scored — what the progress poll keys off. */
export function isEvaluationActive(status: EvaluationStatus): boolean {
	return status === "Queued" || status === "Running";
}

/** An evaluation is usable as a comparison input once it has finished scoring at least one sample. */
export function isEvaluationUsable(evaluation: EvaluationRun | null | undefined): boolean {
	return evaluation != null && evaluation.status === "Succeeded" && evaluation.scoredCount > 0;
}

/** Accuracy as a percentage string, or a dash when nothing was scored — never a misleading "0%". */
export function formatAccuracy(accuracy: number, scored: number): string {
	return scored === 0 ? "—" : `${(accuracy * 100).toFixed(1)}%`;
}

/** A signed percentage-point delta, so an improvement reads as "+12.5pp" rather than "0.125". */
export function formatDelta(delta: number): string {
	const points = delta * 100;
	return `${points >= 0 ? "+" : ""}${points.toFixed(1)}pp`;
}

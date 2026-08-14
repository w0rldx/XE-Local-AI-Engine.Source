import type {
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkProjectDetailResponse as ProjectDetailResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkProjectSummaryResponse as ProjectSummaryResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRunDetailResponse as RunDetailResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRunSummaryResponse as RunSummaryResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1EligibleBenchmarkModelResponse as EligibleModelResponse,
} from "@/core/api/generated";
import type {
	BenchmarkEligibleModel,
	BenchmarkJudgeResult,
	BenchmarkJudgeStatus,
	BenchmarkOrigin,
	BenchmarkOutputPart,
	BenchmarkPrimaryStatus,
	BenchmarkProjectDetail,
	BenchmarkProjectSummary,
	BenchmarkRunDetail,
	BenchmarkRunSummary,
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

export function toBenchmarkProjectDetail(value: ProjectDetailResponse): BenchmarkProjectDetail {
	return {
		...toBenchmarkProjectSummary(value),
		coreTask: value.coreTask,
		judgeModelName: value.judgeModelName ?? null,
		judgeContextTokens: value.judgeContextTokens ?? null,
		judgePromptVersion: numberValue(value.judgePromptVersion, 1),
		judgeOutputSchemaVersion: numberValue(value.judgeOutputSchemaVersion, 1),
	};
}

const primaryStatus = (value: RunSummaryResponse["primaryStatus"]): BenchmarkPrimaryStatus => value ?? "Failed";
const judgeStatus = (value: RunSummaryResponse["judgeStatus"]): BenchmarkJudgeStatus => value ?? "Failed";

export function toBenchmarkRunSummary(value: RunSummaryResponse): BenchmarkRunSummary {
	return {
		id: value.id ?? "",
		projectId: value.projectId ?? "",
		primaryModelName: value.primaryModelName,
		primaryModelOrigin: origin(value.primaryModelOrigin),
		modelContentFingerprint: value.modelContentFingerprint,
		agentName: value.agentName,
		agentVersion: numberValue(value.agentVersion),
		requestedContextTokens: numberValue(value.requestedContextTokens),
		primaryStatus: primaryStatus(value.primaryStatus),
		judgeStatus: judgeStatus(value.judgeStatus),
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
function judgeResult(value: RunDetailResponse["judgeResult"]): BenchmarkJudgeResult | null {
	if (!value) {
		return null;
	}
	return {
		schemaVersion: numberValue(value.schemaVersion, 1),
		score: numberValue(value.score),
		rationale: value.rationale ?? "",
		judgeModelContentFingerprint: value.judgeModelContentFingerprint ?? "",
		promptVersion: numberValue(value.promptVersion, 1),
	};
}

export function toBenchmarkRunDetail(value: RunDetailResponse): BenchmarkRunDetail {
	return {
		...toBenchmarkRunSummary(value),
		outputParts: outputParts(value.outputParts),
		judgeResult: judgeResult(value.judgeResult),
		primaryErrorMessage: value.primaryErrorMessage ?? null,
		judgeErrorMessage: value.judgeErrorMessage ?? null,
		startedAtUtc: value.startedAtUtc ?? null,
		primaryCompletedAtUtc: value.primaryCompletedAtUtc ?? null,
		judgeStartedAtUtc: value.judgeStartedAtUtc ?? null,
		judgeCompletedAtUtc: value.judgeCompletedAtUtc ?? null,
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

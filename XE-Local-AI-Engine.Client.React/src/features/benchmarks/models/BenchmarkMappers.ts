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
const text = (value: unknown): string => (typeof value === "string" ? value : "");
const numberValue = (value: unknown, fallback = 0): number => (typeof value === "number" ? value : fallback);
const nullableNumber = (value: unknown): number | null => (typeof value === "number" ? value : null);
const nullableText = (value: unknown): string | null => (typeof value === "string" ? value : null);

export function toBenchmarkProjectSummary(value: Record<string, unknown>): BenchmarkProjectSummary {
	return {
		id: text(value["id"]),
		name: text(value["name"]),
		contextTokens: numberValue(value["contextTokens"]),
		agentDefinitionId: text(value["agentDefinitionId"]),
		judgeEnabled: value["judgeEnabled"] === true,
		runCount: numberValue(value["runCount"]),
		isFrozen: value["isFrozen"] === true,
		version: numberValue(value["version"]),
		createdAtUtc: numberValue(value["createdAtUtc"]),
		updatedAtUtc: numberValue(value["updatedAtUtc"]),
	};
}

export function toBenchmarkProjectDetail(value: Record<string, unknown>): BenchmarkProjectDetail {
	return {
		...toBenchmarkProjectSummary(value),
		coreTask: text(value["coreTask"]),
		judgeModelName: nullableText(value["judgeModelName"]),
		judgeContextTokens: nullableNumber(value["judgeContextTokens"]),
		judgePromptVersion: numberValue(value["judgePromptVersion"], 1),
		judgeOutputSchemaVersion: numberValue(value["judgeOutputSchemaVersion"], 1),
	};
}

function primaryStatus(value: unknown): BenchmarkPrimaryStatus {
	return ["Queued", "Running", "CancelRequested", "Succeeded", "Failed", "Cancelled"].includes(String(value))
		? (value as BenchmarkPrimaryStatus)
		: "Failed";
}
function judgeStatus(value: unknown): BenchmarkJudgeStatus {
	return ["Disabled", "Pending", "Skipped", "Queued", "Running", "Succeeded", "Failed", "Cancelled"].includes(String(value))
		? (value as BenchmarkJudgeStatus)
		: "Failed";
}

export function toBenchmarkRunSummary(value: Record<string, unknown>): BenchmarkRunSummary {
	return {
		id: text(value["id"]),
		projectId: text(value["projectId"]),
		primaryModelName: text(value["primaryModelName"]),
		primaryModelOrigin: origin(value["primaryModelOrigin"]),
		modelContentFingerprint: text(value["modelContentFingerprint"]),
		agentName: text(value["agentName"]),
		agentVersion: numberValue(value["agentVersion"]),
		requestedContextTokens: numberValue(value["requestedContextTokens"]),
		primaryStatus: primaryStatus(value["primaryStatus"]),
		judgeStatus: judgeStatus(value["judgeStatus"]),
		effectiveContextTokens: nullableNumber(value["effectiveContextTokens"]),
		durationMs: nullableNumber(value["durationMs"]),
		totalTokens: nullableNumber(value["totalTokens"]),
		tokensPerSecond: nullableNumber(value["tokensPerSecond"]),
		userScore: nullableNumber(value["userScore"]),
		lastStreamSequence: numberValue(value["lastStreamSequence"]),
		version: numberValue(value["version"]),
		createdAtUtc: numberValue(value["createdAtUtc"]),
		updatedAtUtc: numberValue(value["updatedAtUtc"]),
	};
}

function outputParts(value: unknown): BenchmarkOutputPart[] {
	if (!Array.isArray(value)) {
		return [];
	}
	return value.filter((part): part is BenchmarkOutputPart => typeof part === "object" && part !== null && "kind" in part);
}
function judgeResult(value: unknown): BenchmarkJudgeResult | null {
	if (!value || typeof value !== "object") {
		return null;
	}
	const result = value as Record<string, unknown>;
	return {
		schemaVersion: numberValue(result["schemaVersion"], 1),
		score: numberValue(result["score"]),
		rationale: text(result["rationale"]),
		judgeModelContentFingerprint: text(result["judgeModelContentFingerprint"]),
		promptVersion: numberValue(result["promptVersion"], 1),
	};
}

export function toBenchmarkRunDetail(value: Record<string, unknown>): BenchmarkRunDetail {
	return {
		...toBenchmarkRunSummary(value),
		outputParts: outputParts(value["outputParts"]),
		judgeResult: judgeResult(value["judgeResult"]),
		primaryErrorMessage: nullableText(value["primaryErrorMessage"]),
		judgeErrorMessage: nullableText(value["judgeErrorMessage"]),
		startedAtUtc: nullableNumber(value["startedAtUtc"]),
		primaryCompletedAtUtc: nullableNumber(value["primaryCompletedAtUtc"]),
		judgeStartedAtUtc: nullableNumber(value["judgeStartedAtUtc"]),
		judgeCompletedAtUtc: nullableNumber(value["judgeCompletedAtUtc"]),
	};
}

export const toBenchmarkEligibleModel = (value: Record<string, unknown>): BenchmarkEligibleModel => ({
	modelName: text(value["modelName"]),
	maxContextTokens: nullableNumber(value["maxContextTokens"]),
	effectiveContextTokens: nullableNumber(value["effectiveContextTokens"]),
	origin: origin(value["origin"]),
	modelContentFingerprint: text(value["modelContentFingerprint"]),
	supportsTools: value["supportsTools"] === true,
});

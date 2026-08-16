import type {
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkProjectDetailResponse as ProjectDetailResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkProjectSummaryResponse as ProjectSummaryResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRunDetailResponse as RunDetailResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRunSummaryResponse as RunSummaryResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1EligibleBenchmarkModelResponse as EligibleModelResponse,
} from "@/core/api/generated";
import type {
	BenchmarkEligibleModel,
	BenchmarkEvidenceObject,
	BenchmarkFlashAttentionMode,
	BenchmarkJudgeResult,
	BenchmarkJudgeStatus,
	BenchmarkKvCacheTypeSource,
	BenchmarkLaunchFacts,
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
		judgeModelName: value.judge?.modelName ?? null,
		judgeContextTokens: value.judge?.requestedContextTokens ?? null,
		judgePromptVersion: 2,
		judgeOutputSchemaVersion: 2,
	};
}

const primaryStatus = (value: RunSummaryResponse["primaryStatus"]): BenchmarkPrimaryStatus => value ?? "Failed";
// The wire says the judging's own state in lowercase; the UI's vocabulary still carries the run-level words it has
// always rendered. S3 replaces the UI shape; until then this is the one place the two vocabularies meet.
const judgeStatus = (value: RunSummaryResponse["judge"]): BenchmarkJudgeStatus => {
	switch (value?.state) {
		case "queued":
			return "Queued";
		case "running":
			return "Running";
		case "succeeded":
			return "Succeeded";
		case "failed":
			return "Failed";
		case "cancelled":
			return "Cancelled";
		default:
			return "Disabled";
	}
};

// The judge's launch evidence moved onto its attempt and is not on the run's wire shape any more. Every member is
// nullable by contract, so an all-null block renders as "—" exactly like a run frozen before receipts existed.
const emptyLaunchFacts = (): BenchmarkLaunchFacts => ({
	variant: null,
	kvCacheType: null,
	kvCacheTypeSource: null,
	kvAutoReason: null,
	flashAttentionMode: null,
	intendedLaunchIdentity: null,
	intendedExecutableSha256: null,
	effectiveLaunchIdentity: null,
	effectiveBackend: null,
	placementOffloaded: null,
	placementTotal: null,
	executableSha256: null,
	hasAuxAssets: null,
	receiptHash: null,
	environmentFactsHash: null,
});

const text = (value: unknown): string | null => (typeof value === "string" && value.length > 0 ? value : null);
const count = (value: unknown): number | null => (typeof value === "number" && Number.isFinite(value) ? value : null);
const flag = (value: unknown): boolean | null => (typeof value === "boolean" ? value : null);
const kvSource = (value: unknown): BenchmarkKvCacheTypeSource | null => (value === "explicit" || value === "auto" ? value : null);
const flashAttention = (value: unknown): BenchmarkFlashAttentionMode | null =>
	value === "auto" || value === "on" ? value : null;
const evidenceObject = (value: unknown): BenchmarkEvidenceObject | null =>
	typeof value === "object" && value !== null && !Array.isArray(value) ? (value as BenchmarkEvidenceObject) : null;

// The two launch sides carry the identical column set under a `primary…`/`judge…` prefix, so one prefix-driven reader
// covers both. Every member is nullable by contract (D7: legacy rows predate the receipt and stay NULL).
function launchFacts(value: RunSummaryResponse, prefix: "primary"): BenchmarkLaunchFacts {
	const at = (suffix: string): unknown => (value as Record<string, unknown>)[`${prefix}${suffix}`];
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
		primaryLaunch: launchFacts(value, "primary"),
		judgeLaunch: emptyLaunchFacts(),
		id: value.id ?? "",
		projectId: value.projectId ?? "",
		primaryModelName: value.primaryModelName,
		primaryModelOrigin: origin(value.primaryModelOrigin),
		modelContentFingerprint: value.modelContentFingerprint,
		agentName: value.agentName,
		agentVersion: numberValue(value.agentVersion),
		requestedContextTokens: numberValue(value.requestedContextTokens),
		primaryStatus: primaryStatus(value.primaryStatus),
		judgeStatus: judgeStatus(value.judge),
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
// The rubric verdict flattened into the shape the current panel renders: the summary is the rationale until S3 shows
// the per-criterion breakdown the wire already carries.
function judgeResult(value: RunDetailResponse["judge"]): BenchmarkJudgeResult | null {
	if (value?.state !== "succeeded") {
		return null;
	}
	return {
		schemaVersion: 2,
		score: numberValue(value.score ?? undefined),
		rationale: value.summary ?? "",
		judgeModelContentFingerprint: "",
		promptVersion: 2,
	};
}

export function toBenchmarkRunDetail(value: RunDetailResponse): BenchmarkRunDetail {
	return {
		...toBenchmarkRunSummary(value),
		primaryLaunchReceipt: evidenceObject(value.primaryLaunchReceipt),
		judgeLaunchReceipt: null,
		primaryEnvironmentFacts: evidenceObject(value.primaryEnvironmentFacts),
		judgeEnvironmentFacts: null,
		outputParts: outputParts(value.outputParts),
		judgeResult: judgeResult(value.judge),
		primaryErrorMessage: value.primaryErrorMessage ?? null,
		judgeErrorMessage: value.judge?.errorMessage ?? null,
		startedAtUtc: value.startedAtUtc ?? null,
		primaryCompletedAtUtc: value.primaryCompletedAtUtc ?? null,
		judgeStartedAtUtc: null,
		judgeCompletedAtUtc: null,
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

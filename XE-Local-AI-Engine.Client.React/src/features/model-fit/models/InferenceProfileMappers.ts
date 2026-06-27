import type {
	XeLocalAiEngineClientEndpointsModelFitV1BenchmarkInferenceProfileResponse,
	XeLocalAiEngineClientEndpointsModelFitV1InferenceBenchmarkMetricsDto,
	XeLocalAiEngineClientEndpointsModelFitV1InferenceProfileViewDto,
	XeLocalAiEngineClientEndpointsModelFitV1ListInferenceProfilesResponse,
} from "@/core/api/generated";
import {
	type InferenceBenchmarkMetrics,
	type InferenceBenchmarkResult,
	type InferenceProfileView,
	toInferenceProfileStatus,
} from "@/features/model-fit/models/InferenceProfileModels";

// Maps the generated (OpenAPI) inference-profile response types to the stricter domain view-models. The generated
// types are the single source of truth for the wire shape; their fields are all optional, so each mapper coalesces
// to a required value with a safe default. The wire DTO additionally carries tuned launch flags (nGpuLayers,
// tensorSplit, overrideTensor, kvType*, flashAttn) — these are DELIBERATELY NOT mapped: the operator surface shows
// outcomes (status / tok-s / VRAM), never raw flags. There is no machine key on the wire, and none here.

function toInferenceProfileView(dto: XeLocalAiEngineClientEndpointsModelFitV1InferenceProfileViewDto): InferenceProfileView {
	return {
		id: dto.id ?? "",
		modelName: dto.modelName ?? "",
		role: dto.role ?? null,
		backend: dto.backend ?? "",
		status: toInferenceProfileStatus(dto.status),
		quant: dto.quant ?? null,
		ctxSize: dto.ctxSize ?? null,
		isMoe: dto.isMoe ?? false,
		expertCount: dto.expertCount ?? null,
		// A frozen profile records the snapshot id it was benchmarked against; its presence gates the Freeze action.
		hasBenchmark: (dto.benchmarkSnapshotId ?? null) !== null,
		frozenVramBytes: dto.freeVramAtFreezeBytes ?? null,
	};
}

export function toInferenceProfileViews(
	dto: XeLocalAiEngineClientEndpointsModelFitV1ListInferenceProfilesResponse,
): readonly InferenceProfileView[] {
	return (dto.items ?? []).map(toInferenceProfileView);
}

function toInferenceBenchmarkMetrics(
	dto: XeLocalAiEngineClientEndpointsModelFitV1InferenceBenchmarkMetricsDto,
): InferenceBenchmarkMetrics {
	return {
		tokensPerSecond: dto.tokensPerSecond ?? null,
		ppTokensPerSecond: dto.ppTokensPerSecond ?? null,
		ttftMs: dto.ttftMs ?? null,
		totalLatencyMs: dto.totalLatencyMs ?? null,
		cacheHitRate: dto.cacheHitRate ?? null,
		toolLoopMs: dto.toolLoopMs ?? null,
		vramLoadBytes: dto.vramLoadBytes ?? null,
		vramAfterBytes: dto.vramAfterBytes ?? null,
		runs: dto.runs ?? null,
	};
}

// Maps a benchmark response. The generated mutationFn is optional, so the response may be undefined — return an
// empty result rather than throwing, and coalesce a missing metrics object to null so the card omits it cleanly.
export function toBenchmarkResult(
	dto: XeLocalAiEngineClientEndpointsModelFitV1BenchmarkInferenceProfileResponse | undefined,
): InferenceBenchmarkResult {
	return {
		snapshotId: dto?.snapshotId ?? null,
		metrics: dto?.metrics ? toInferenceBenchmarkMetrics(dto.metrics) : null,
		profile: dto?.profile ? toInferenceProfileView(dto.profile) : null,
	};
}

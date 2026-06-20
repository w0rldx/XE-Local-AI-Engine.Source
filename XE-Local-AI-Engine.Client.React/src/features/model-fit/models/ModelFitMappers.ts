import type {
	XeLocalAiEngineClientEndpointsModelFitV1GetLatestRecommendationsResponse,
	XeLocalAiEngineClientEndpointsModelFitV1GgufRepositoryFileResponse,
	XeLocalAiEngineClientEndpointsModelFitV1GgufRepositoryResponse,
	XeLocalAiEngineClientEndpointsModelFitV1HardwareProfileResponse,
	XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse,
	XeLocalAiEngineClientEndpointsModelFitV1LlamaCppVersionResponse,
	XeLocalAiEngineClientEndpointsModelFitV1ModelFitRecommendationResponse,
	XeLocalAiEngineClientEndpointsModelFitV1RunningModelResponse,
} from "@/core/api/generated";
import {
	type GgufRepository,
	type GgufRepositoryDetail,
	type GgufRepositoryFile,
	type HardwareGpuVendor,
	hardwareGpuVendors,
	type HardwareProfile,
	type LlamaCppVersion,
	type ModelFitLatestRecommendations,
	type ModelFitRecommendation,
	type RunningModel,
} from "@/features/model-fit/models/ModelFitModels";

// Maps the generated (OpenAPI) model-fit response types to the stricter domain view-models the pages depend on.
// The generated types are the single source of truth for the wire shape; their fields are all optional (`x?: T`),
// so each mapper coalesces every field to a required value with a safe default. The DTOs carry only sanitized
// fields (no raw advisor JSON / stderr / diagnostics blobs, no HF token); redaction is the backend's — the mapper
// only surfaces what the API returns and never reconstructs a dropped field.

function toModelFitRecommendation(
	dto: XeLocalAiEngineClientEndpointsModelFitV1ModelFitRecommendationResponse,
): ModelFitRecommendation {
	return {
		rank: dto.rank ?? 0,
		modelName: dto.modelName ?? "",
		providerModelName: dto.providerModelName ?? null,
		score: dto.score ?? 0,
		fitLevel: dto.fitLevel ?? null,
		runMode: dto.runMode ?? null,
		quantization: dto.quantization ?? null,
		estimatedTokensPerSecond: dto.estimatedTokensPerSecond ?? null,
		requiredRamMb: dto.requiredRamMb ?? null,
		requiredVramMb: dto.requiredVramMb ?? null,
		contextTokens: dto.contextTokens ?? null,
		isInstalled: dto.isInstalled ?? false,
		pullModelName: dto.pullModelName ?? null,
		releaseDate: dto.releaseDate ?? null,
	};
}

export function toLatestRecommendations(
	dto: XeLocalAiEngineClientEndpointsModelFitV1GetLatestRecommendationsResponse,
): ModelFitLatestRecommendations {
	return {
		hasCache: dto.hasCache ?? false,
		snapshotId: dto.snapshotId ?? null,
		status: dto.status ?? null,
		useCase: dto.useCase ?? null,
		lastRefreshedAtUtc: dto.lastRefreshedAtUtc ?? null,
		// hasCache:false carries an empty recommendations array; coalesce defensively in case it is omitted.
		recommendations: (dto.recommendations ?? []).map(toModelFitRecommendation),
	};
}

// Narrows the generated `gpuVendor` string back to the domain union with a runtime guard, so a future backend
// vendor value is normalized to "unknown" rather than smuggled in as an out-of-union value (which a downstream
// badge map / exhaustive switch would mishandle silently).
function toGpuVendor(value: string | undefined): HardwareGpuVendor {
	return (hardwareGpuVendors as readonly string[]).includes(value ?? "") ? (value as HardwareGpuVendor) : "unknown";
}

export function toHardwareProfile(
	dto: XeLocalAiEngineClientEndpointsModelFitV1HardwareProfileResponse,
): HardwareProfile {
	return {
		totalRamBytes: dto.totalRamBytes ?? 0,
		availableRamBytes: dto.availableRamBytes ?? 0,
		vramBytes: dto.vramBytes ?? null,
		vramKnown: dto.vramKnown ?? false,
		gpuVendor: toGpuVendor(dto.gpuVendor),
		gpuAccelAvailable: dto.gpuAccelAvailable ?? false,
		cpuCores: dto.cpuCores ?? 0,
		freeDiskBytes: dto.freeDiskBytes ?? 0,
	};
}

export function toGgufRepository(dto: XeLocalAiEngineClientEndpointsModelFitV1GgufRepositoryResponse): GgufRepository {
	return {
		repoId: dto.repoId ?? "",
		isGated: dto.isGated ?? false,
		downloads: dto.downloads ?? 0,
		likes: dto.likes ?? 0,
		lastModifiedAtUtc: dto.lastModifiedAtUtc ?? null,
		license: dto.license ?? null,
		hasUsableGguf: dto.hasUsableGguf ?? false,
	};
}

function toGgufRepositoryFile(
	dto: XeLocalAiEngineClientEndpointsModelFitV1GgufRepositoryFileResponse,
): GgufRepositoryFile {
	return {
		fileName: dto.fileName ?? "",
		quant: dto.quant ?? "",
		isDynamic: dto.isDynamic ?? false,
		sizeBytes: dto.sizeBytes ?? 0,
	};
}

export function toGgufRepositoryDetail(
	dto: XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse,
): GgufRepositoryDetail {
	return {
		repoId: dto.repoId ?? "",
		// A discovery failure returns an empty file list (200); coalesce defensively in case it is omitted.
		files: (dto.files ?? []).map(toGgufRepositoryFile),
	};
}

export function toRunningModel(dto: XeLocalAiEngineClientEndpointsModelFitV1RunningModelResponse): RunningModel {
	return {
		modelName: dto.modelName ?? "",
		role: dto.role ?? "",
		isResponsive: dto.isResponsive ?? false,
		detail: dto.detail ?? "",
	};
}

export function toLlamaCppVersion(
	dto: XeLocalAiEngineClientEndpointsModelFitV1LlamaCppVersionResponse,
): LlamaCppVersion {
	return {
		version: dto.version ?? "",
		variant: dto.variant ?? "",
		isPinnedFallback: dto.isPinnedFallback ?? false,
		pinnedTag: dto.pinnedTag ?? "",
	};
}

import type {
	XeLocalAiEngineClientEndpointsModelFitV1GetLatestRecommendationsResponse,
	XeLocalAiEngineClientEndpointsModelFitV1HardwareProfileResponse,
	XeLocalAiEngineClientEndpointsModelFitV1ModelFitRecommendationResponse,
} from "@/core/api/generated";
import {
	type HardwareGpuVendor,
	type HardwareProfile,
	hardwareGpuVendors,
	type ModelFitLatestRecommendations,
	type ModelFitRecommendation,
} from "@/features/model-fit/models/ModelFitModels";

// Maps the generated (OpenAPI) model-fit response types to the stricter domain view-models the advisor depends on.
// The generated types are the single source of truth for the wire shape; their fields are all optional (`x?: T`),
// so each mapper coalesces every field to a required value with a safe default. The DTOs carry only sanitized
// fields (no raw advisor JSON / stderr / diagnostics blobs); redaction is the backend's — the mapper only surfaces
// what the API returns and never reconstructs a dropped field.

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
		isTrustedPublisher: dto.isTrustedPublisher ?? false,
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

export function toHardwareProfile(dto: XeLocalAiEngineClientEndpointsModelFitV1HardwareProfileResponse): HardwareProfile {
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

import type {
	XeLocalAiEngineClientEndpointsModelFitV1GetLatestRecommendationsResponse,
	XeLocalAiEngineClientEndpointsModelFitV1HardwareProfileResponse,
	XeLocalAiEngineClientEndpointsModelFitV1ModelCatalogInfoResponse,
	XeLocalAiEngineClientEndpointsModelFitV1ModelFitRecommendationResponse,
} from "@/core/api/generated";
import {
	type HardwareGpuVendor,
	type HardwareProfile,
	hardwareGpuVendors,
	type ModelFitCatalogInfo,
	type ModelFitCatalogSource,
	modelFitCatalogSources,
	type ModelFitCatalogTier,
	type ModelFitLatestRecommendations,
	type ModelFitRecommendation,
	type ModelFitRecommendationSection,
	modelFitRecommendationSections,
} from "@/features/model-fit/models/ModelFitModels";

// Narrows the generated `section` string back to the domain union with a runtime guard, so a future/unexpected
// backend section value degrades to "explore" (the least-prominent group) rather than crashing the section split.
function toRecommendationSection(value: string): ModelFitRecommendationSection {
	return (modelFitRecommendationSections as readonly string[]).includes(value)
		? (value as ModelFitRecommendationSection)
		: "explore";
}

// Narrows the generated `tier` string back to the domain S/A/B union, defaulting to null (no tier) for an absent or
// out-of-union value rather than smuggling an unrecognized tier into the badge map.
function toCatalogTier(value: string | null | undefined): ModelFitCatalogTier {
	return value === "S" || value === "A" || value === "B" ? value : null;
}

// Narrows the generated catalog `source` string back to the domain union, defaulting to "bundled" (the safe
// baseline) for a future/unexpected source value.
function toCatalogSource(value: string): ModelFitCatalogSource {
	return (modelFitCatalogSources as readonly string[]).includes(value) ? (value as ModelFitCatalogSource) : "bundled";
}

// Maps optional generated wire fields into required domain values; validation remains at the API boundary.
// Only API-projected sanitized fields are exposed.

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
		section: toRecommendationSection(dto.section),
		tier: toCatalogTier(dto.tier),
		catalogId: dto.catalogId ?? null,
		catalogDisplayName: dto.catalogDisplayName ?? null,
		catalogNotes: dto.catalogNotes ?? null,
		expertsOffloaded: dto.expertsOffloaded ?? false,
		gpuGb: dto.gpuGb ?? null,
		cpuGb: dto.cpuGb ?? null,
		kvQuant: dto.kvQuant ?? null,
		kvQuantEstimatedGb: dto.kvQuantEstimatedGb ?? null,
		kvQuantHeadroomGb: dto.kvQuantHeadroomGb ?? null,
		kvQuantFits: dto.kvQuantFits ?? null,
		kvQuantRequiresFlashAttention: dto.kvQuantRequiresFlashAttention ?? null,
		kvBytesPerToken: dto.kvBytesPerToken ?? null,
		kvBytesPerTokenQuant: dto.kvBytesPerTokenQuant ?? null,
		attentionArch: dto.attentionArch ?? null,
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
		inferenceBackend: dto.inferenceBackend ?? "unknown",
		gpuExpected: dto.gpuExpected ?? false,
		cpuFallback: dto.cpuFallback ?? false,
		cpuFallbackReason: dto.cpuFallbackReason ?? null,
		cpuFallbackRemediation: dto.cpuFallbackRemediation ?? null,
		backendUndeterminedReason: dto.backendUndeterminedReason ?? null,
		gpuOffloadedLayers: dto.gpuOffloadedLayers ?? null,
		gpuTotalLayers: dto.gpuTotalLayers ?? null,
		gpuOffloadModelName: dto.gpuOffloadModelName ?? null,
		gpuOffloadRole: dto.gpuOffloadRole ?? null,
	};
}

export function toModelFitCatalogInfo(dto: XeLocalAiEngineClientEndpointsModelFitV1ModelCatalogInfoResponse): ModelFitCatalogInfo {
	return {
		catalogVersion: dto.catalogVersion ?? "",
		updatedAt: dto.updatedAt ?? null,
		source: toCatalogSource(dto.source),
		fetchedAtUtc: dto.fetchedAtUtc ?? null,
		sourceUrl: dto.sourceUrl ?? null,
		modelCount: dto.modelCount ?? 0,
		refreshSourceConfigured: dto.refreshSourceConfigured ?? false,
	};
}

import type {
	XeLocalAiEngineClientEndpointsModelFitV1ApprovedImageResponse,
	XeLocalAiEngineClientEndpointsModelFitV1GetLatestRecommendationsResponse,
	XeLocalAiEngineClientEndpointsModelFitV1ModelFitRecommendationResponse,
} from "@/core/api/generated";
import type {
	ApprovedImage,
	ModelFitImagePurpose,
	ModelFitLatestRecommendations,
	ModelFitRecommendation,
} from "@/features/model-fit/models/ModelFitModels";

// Maps the generated (OpenAPI) model-fit response types to the stricter domain view-models the pages depend on.
// The generated types are the single source of truth for the wire shape; their fields are all optional (`x?: T`),
// so each mapper coalesces every field to a required value with a safe default. The DTOs carry only sanitized
// fields (no raw llmfit JSON / stderr / diagnostics blobs); redaction is the backend's — the mapper only surfaces
// what the API returns and never reconstructs a dropped field.

// The two members of the wire [Flags] enum. Used to narrow the generated `string[]` projection of `purpose` back
// to the domain union with a runtime guard, so a future backend enum addition is dropped rather than smuggled in
// as an out-of-union value (which a downstream badge map / exhaustive switch would mishandle silently).
const MODEL_FIT_IMAGE_PURPOSES: readonly ModelFitImagePurpose[] = ["ModelRecommendation", "ModelBenchmark"];

function isModelFitImagePurpose(value: string): value is ModelFitImagePurpose {
	return (MODEL_FIT_IMAGE_PURPOSES as readonly string[]).includes(value);
}

export function toApprovedImage(dto: XeLocalAiEngineClientEndpointsModelFitV1ApprovedImageResponse): ApprovedImage {
	return {
		approvedImageId: dto.approvedImageId ?? "",
		displayName: dto.displayName ?? "",
		description: dto.description ?? null,
		// purpose is a [Flags] enum projected to a string array on the wire; the generated type widens it to
		// string[], so filter through the value guard — identical values pass, an unknown future member is dropped.
		purpose: (dto.purpose ?? []).filter(isModelFitImagePurpose),
		imageReference: dto.imageReference ?? "",
		sourceUrl: dto.sourceUrl ?? null,
		upstreamVersion: dto.upstreamVersion ?? null,
		enabled: dto.enabled ?? false,
		deprecatedAtUtc: dto.deprecatedAtUtc ?? null,
		replacementApprovedImageId: dto.replacementApprovedImageId ?? null,
		lastUsedAtUtc: dto.lastUsedAtUtc ?? null,
		lastSuccessfulRunAtUtc: dto.lastSuccessfulRunAtUtc ?? null,
		diagnostics: dto.diagnostics ?? null,
	};
}

export function toModelFitRecommendation(
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
	};
}

export function toLatestRecommendations(
	dto: XeLocalAiEngineClientEndpointsModelFitV1GetLatestRecommendationsResponse,
): ModelFitLatestRecommendations {
	return {
		hasCache: dto.hasCache ?? false,
		snapshotId: dto.snapshotId ?? null,
		status: dto.status ?? null,
		sourceImageId: dto.sourceImageId ?? null,
		useCase: dto.useCase ?? null,
		providerName: dto.providerName ?? null,
		lastRefreshedAtUtc: dto.lastRefreshedAtUtc ?? null,
		// hasCache:false carries an empty recommendations array; coalesce defensively in case it is omitted.
		recommendations: (dto.recommendations ?? []).map(toModelFitRecommendation),
	};
}

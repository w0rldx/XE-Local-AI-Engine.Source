import type {
	XeLocalAiEngineClientEndpointsLocalModelsV1RunningLocalModelResponse,
	XeLocalAiEngineClientEndpointsLocalModelsV1RunningLocalModelsResponse,
	XeLocalAiEngineClientEndpointsLocalModelsV1UnloadLocalModelResponse,
} from "@/core/api/generated";
import type { LoadedModel, LoadedModelsSnapshot, UnloadResult } from "@/features/loaded-models/models/LoadedModelsModels";

// Maps the generated (OpenAPI) running-models response types to the stricter domain view-models the page depends
// on. The generated types are the single source of truth for the wire shape; their fields are all optional
// (`x?: T`), so each mapper coalesces every field to a required value with a safe default. The DTOs carry only
// sanitized fields; the mapper only surfaces what the API returns and never reconstructs a dropped field.

function toLoadedModel(dto: XeLocalAiEngineClientEndpointsLocalModelsV1RunningLocalModelResponse): LoadedModel {
	return {
		modelName: dto.modelName ?? "",
		sizeBytes: dto.sizeBytes ?? null,
		sizeVramBytes: dto.sizeVramBytes ?? null,
		expiresAtUtc: dto.expiresAtUtc ?? null,
	};
}

export function toLoadedModelsSnapshot(
	dto: XeLocalAiEngineClientEndpointsLocalModelsV1RunningLocalModelsResponse,
): LoadedModelsSnapshot {
	return {
		// The backend returns isAvailable:false (not a 500) when the provider is unreachable; default defensively.
		isAvailable: dto.isAvailable ?? false,
		error: dto.error ?? null,
		// When unavailable the list is empty; coalesce defensively in case it is omitted.
		models: (dto.items ?? []).map(toLoadedModel),
	};
}

export function toUnloadResult(dto: XeLocalAiEngineClientEndpointsLocalModelsV1UnloadLocalModelResponse): UnloadResult {
	return {
		modelName: dto.modelName ?? "",
		unloaded: dto.unloaded ?? false,
	};
}

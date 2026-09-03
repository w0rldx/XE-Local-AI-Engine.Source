import type {
	XeLocalAiEngineClientEndpointsLocalModelsV1RunningLocalModelResponse,
	XeLocalAiEngineClientEndpointsLocalModelsV1RunningLocalModelsResponse,
	XeLocalAiEngineClientEndpointsLocalModelsV1UnloadLocalModelResponse,
} from "@/core/api/generated";
import type { LoadedModel, LoadedModelsSnapshot, UnloadResult } from "@/features/loaded-models/models/LoadedModelsModels";

// Maps optional generated wire fields into required domain values; validation remains at the API boundary.
// Only API-projected sanitized fields are exposed.

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
		// Default to TRUE (configured) when the field is absent so an older backend keeps today's polling behavior; a
		// new backend that reports false lets the page stop polling an off/absent Ollama.
		ollamaConfigured: dto.ollamaConfigured ?? true,
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

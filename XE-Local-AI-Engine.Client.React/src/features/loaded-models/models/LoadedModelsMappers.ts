import type { XeLocalAiEngineClientEndpointsLocalModelsV1RunningLocalModelsResponse } from "@/core/api/generated";
import type { LoadedModelsSnapshot } from "@/features/loaded-models/models/LoadedModelsModels";

// Maps optional generated wire fields into required domain values; validation remains at the API boundary.
export function toLoadedModelsSnapshot(
	dto: XeLocalAiEngineClientEndpointsLocalModelsV1RunningLocalModelsResponse,
): LoadedModelsSnapshot {
	return {
		// The backend returns isAvailable:false (not a 500) when the provider is unreachable; default defensively.
		isAvailable: dto.isAvailable ?? false,
		// Default to TRUE (configured) when the field is absent so an older backend keeps today's polling behavior; a
		// new backend that reports false stops the poll against an off/absent Ollama.
		ollamaConfigured: dto.ollamaConfigured ?? true,
		// When unavailable the list is empty; coalesce defensively in case it is omitted.
		models: (dto.items ?? []).map((item) => ({ modelName: item.modelName ?? "" })),
	};
}

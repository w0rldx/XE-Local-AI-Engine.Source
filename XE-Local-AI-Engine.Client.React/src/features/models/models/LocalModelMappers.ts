import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse } from "@/core/api/generated";
import {
	emptyModelValue,
	formatModelModifiedDate,
	formatModelSize,
	type LocalModelViewModel,
} from "@/features/models/models/LocalModelModel";

// Maps the generated (OpenAPI) local-model response types to the stricter domain view-models the page depends on.
// The generated types are the single source of truth for the wire shape; their fields are all optional (`x?: T`),
// so each mapper coalesces every field to a required value with a sensible default. Nothing the backend already
// redacts is reconstructed here — only the fields the API returns surface.

// Projects a generated local-model item into the display view-model (formatted size/date labels, em-dash fallbacks).
export function toLocalModelViewModel(dto: XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse): LocalModelViewModel {
	return {
		modelName: dto.modelName ?? "",
		provider: dto.provider ?? "Ollama",
		origin: dto.origin ?? null,
		sizeLabel: formatModelSize(dto.sizeBytes),
		modifiedDateLabel: formatModelModifiedDate(dto.modifiedAtUtc),
		familyLabel: dto.family?.trim() || emptyModelValue,
		parameterSizeLabel: dto.parameterSize?.trim() || emptyModelValue,
		quantizationLabel: dto.quantizationLevel?.trim() || emptyModelValue,
		isSelected: dto.isSelected ?? false,
		kind: dto.kind ?? "Unknown",
		detectedKind: dto.detectedKind ?? "Unknown",
		capabilities: dto.capabilities ?? [],
		isOverridden: dto.isOverridden ?? false,
	};
}

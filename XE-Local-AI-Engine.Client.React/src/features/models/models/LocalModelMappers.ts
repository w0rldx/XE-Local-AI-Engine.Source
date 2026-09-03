import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse } from "@/core/api/generated";
import { EXTERNAL_PROVIDER } from "@/core/models/LocalModelProviders";
import {
	emptyModelValue,
	formatModelModifiedDate,
	formatModelSize,
	type LocalModelOrigin,
	type LocalModelViewModel,
} from "@/features/models/models/LocalModelModel";

type LocalModelWire = XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse;

function localModelOrigin(value: LocalModelWire["origin"]): LocalModelOrigin | null {
	if (value === "huggingface") {
		return "huggingface";
	}
	if (value === "imported") {
		return "imported";
	}
	return null;
}

// Maps optional generated wire fields into required domain values; validation remains at the API boundary.
// Only API-projected sanitized fields are exposed.

/**
 * True for the models this node actually has INSTALLED — everything the catalog lists except the operator-registered
 * external endpoints.
 *
 * The list endpoint appends external registrations so the chat picker can offer them, but they are not files in the
 * model store: there is nothing to delete, reset a kind override on, or count as installed, and per D10 they are
 * managed only from the External providers page. Every install/lifecycle surface reading this list filters through
 * here so a Delete or Reset action can never be offered against a remote endpoint.
 */
export function isInstalledLocalModel(dto: LocalModelWire): boolean {
	return dto.provider !== EXTERNAL_PROVIDER;
}

// Projects a generated local-model item into the display view-model (formatted size/date labels, em-dash fallbacks).
export function toLocalModelViewModel(dto: LocalModelWire): LocalModelViewModel {
	return {
		modelName: dto.modelName ?? "",
		provider: dto.provider ?? "Ollama",
		origin: localModelOrigin(dto.origin),
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

import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse } from "@/core/api/generated";
import type { ModelOption } from "@/features/chat/models/ChatModels";

// Local alias for the generated REST model response (backend OpenAPI is the single source of truth). Every field
// is optional on the generated type, so each read below coalesces to the prior default.
type LocalModelDto = XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse;

export function toModelOption(model: LocalModelDto, nodeAvailable: boolean): ModelOption {
	const statusLabel = [
		model.isSelected ? "Node default" : undefined,
		model.parameterSize ?? undefined,
		model.quantizationLevel ?? undefined,
	]
		.filter((part): part is string => Boolean(part))
		.join(" · ");

	const modelName = model.modelName ?? "";
	return {
		value: modelName,
		label: modelName,
		// Per-model capabilities (Ollama `/api/show`): thinking → reasoning menu; tools → local-tool controls.
		// Coalesce the optional generated booleans to false so a model that omits them is treated as not capable.
		isReasoningModel: model.isReasoningCapable ?? false,
		isToolCapable: model.isToolCapable ?? false,
		isAvailable: nodeAvailable,
		statusLabel: statusLabel.length > 0 ? statusLabel : undefined,
		// Carry the serving runtime so the page can gate the model-details poll per provider.
		provider: model.provider ?? undefined,
	};
}

// Strict picker filter (locked decision D3): only chat-capable local models reach the composer's model selector.
// Embedding and Unknown models are hidden because they have no completion head and reject the chat endpoint.
// CodexOAuth provider entries are excluded here — they appear in the separate cloud section via useCodexModelOptions.
// Lives in its own module (not Chat.tsx) so it is unit-testable and so exporting it does not break the
// component-only-export Fast Refresh rule on the page.
export function toChatModelOptions(models: LocalModelDto[], nodeAvailable: boolean): ModelOption[] {
	return models
		.filter((model) => model.kind === "Chat" && model.provider !== "CodexOAuth")
		.map((model) => toModelOption(model, nodeAvailable));
}

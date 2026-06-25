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

// Strict picker filter: only chat-capable local models reach the composer's model selector.
// Embedding and Unknown models are hidden because they have no completion head and reject the chat endpoint.
// CodexOAuth provider entries are excluded here — they appear in the separate cloud section via useCodexModelOptions.
// Lives in its own module (not Chat.tsx) so it is unit-testable and so exporting it does not break the
// component-only-export Fast Refresh rule on the page.
export function toChatModelOptions(models: LocalModelDto[], nodeAvailable: boolean): ModelOption[] {
	return models
		.filter((model) => model.kind === "Chat" && model.provider !== "CodexOAuth")
		.map((model) => toModelOption(model, nodeAvailable));
}

// Capabilities the synthetic "Local default" composer option should advertise, derived from the concrete model the
// runtime will actually resolve. Mirrors backend LocalDefaultChatModelResolver: the node default (isSelected) when it
// is an installed chat model, else the fallback `OrderByDescending(modifiedAtUtc).ThenBy(modelName)` pick (newest
// modified first, then name ascending). Picking "Local default" then offers the exact same reasoning/tool controls as
// picking that concrete model directly. `modifiedAtUtc` is an epoch number on the DTO, so it is compared numerically
// (a missing value sorts oldest). Coalesces the optional generated booleans to false.
export function resolveLocalDefaultModelCapabilities(models: LocalModelDto[]): {
	isReasoningModel: boolean;
	isToolCapable: boolean;
} {
	const chatModels = models.filter((model) => model.kind === "Chat" && model.provider !== "CodexOAuth");
	const resolved =
		chatModels.find((model) => model.isSelected) ??
		[...chatModels].sort((a, b) => {
			// Mirror backend fallback: newest modified first, then name ascending. Treat a missing modifiedAtUtc as
			// oldest (-Infinity) so it sorts last under the descending order.
			const am = a.modifiedAtUtc ?? Number.NEGATIVE_INFINITY;
			const bm = b.modifiedAtUtc ?? Number.NEGATIVE_INFINITY;
			if (am !== bm) {
				return bm - am; // descending by modifiedAtUtc
			}
			return (a.modelName ?? "").localeCompare(b.modelName ?? "");
		})[0];
	return {
		isReasoningModel: resolved?.isReasoningCapable ?? false,
		isToolCapable: resolved?.isToolCapable ?? false,
	};
}

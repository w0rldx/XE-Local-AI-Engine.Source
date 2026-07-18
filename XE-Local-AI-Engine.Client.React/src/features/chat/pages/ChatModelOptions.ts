import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse } from "@/core/api/generated";
import type { ModelOption } from "@/features/chat/models/ChatModels";
import { localDefaultModelValue } from "@/features/chat/models/NodeChatModelSelection";

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

// Cloud-provider tags carried on list entries that render in the separate cloud sections of the picker
// (via useCloudModelOptions), not the local list. Excluded here so they appear once, in their cloud group.
const CLOUD_PROVIDERS = new Set(["CodexOAuth", "AzureFoundry"]);

// Strict picker filter: only chat-capable local models reach the composer's model selector.
// Embedding and Unknown models are hidden because they have no completion head and reject the chat endpoint.
// Cloud provider entries (CodexOAuth / AzureFoundry) are excluded here — they appear in the separate cloud
// sections via useCloudModelOptions. Lives in its own module (not Chat.tsx) so it is unit-testable and so
// exporting it does not break the component-only-export Fast Refresh rule on the page.
export function toChatModelOptions(models: LocalModelDto[], nodeAvailable: boolean): ModelOption[] {
	return models
		.filter((model) => model.kind === "Chat" && !CLOUD_PROVIDERS.has(model.provider ?? ""))
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
	const chatModels = models.filter((model) => model.kind === "Chat" && !CLOUD_PROVIDERS.has(model.provider ?? ""));
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

// True when the composer's local model list has resolved to nothing but the synthetic "Local default" entry —
// i.e. no installed chat-capable GGUF model exists on the node. Shared by ModelSelectorCard (explains an
// otherwise-bare picker) and the chat page (pre-empts the first-send ModelNotInstalled failure with inline
// guidance, UX-09). Takes the already-built `modelOptions` list (not the raw DTOs) so both call sites derive
// from the same array Chat.tsx already computes.
export function hasNoLocalChatModels(modelOptions: ModelOption[]): boolean {
	return modelOptions.every((option) => option.value === localDefaultModelValue);
}

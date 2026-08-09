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
		// Per-model capabilities (Ollama `/api/show`): thinking → graded reasoning menu; tools → local-tool controls.
		// `isNativeReasoningCapable` is the SECOND, distinct reasoning capability (harmony/gpt-oss): it renders its own
		// picker badge but keeps the binary On/Off effort vocabulary, so it is deliberately NOT folded into
		// `isReasoningModel` — doing so would route the model into the graded think:<level> path it cannot honor.
		// Coalesce the optional generated booleans to false so a model that omits them is treated as not capable.
		isReasoningModel: model.isReasoningCapable ?? false,
		isNativeReasoningModel: model.isNativeReasoningCapable ?? false,
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

export function isLocalChatModel(model: LocalModelDto | undefined): model is LocalModelDto {
	return model?.kind === "Chat" && !CLOUD_PROVIDERS.has(model.provider ?? "");
}

// Strict picker filter: only chat-capable local models reach the composer's model selector.
// Embedding and Unknown models are hidden because they have no completion head and reject the chat endpoint.
// Cloud provider entries (CodexOAuth / AzureFoundry) are excluded here — they appear in the separate cloud
// sections via useCloudModelOptions. Lives in its own module (not Chat.tsx) so it is unit-testable and so
// exporting it does not break the component-only-export Fast Refresh rule on the page.
export function toChatModelOptions(models: LocalModelDto[], nodeAvailable: boolean): ModelOption[] {
	return models.filter(isLocalChatModel).map((model) => toModelOption(model, nodeAvailable));
}

// Models eligible as the node's speculative-decoding DRAFT model. Two kinds qualify and neither belongs in the chat
// picker's list alone: a purpose-built drafter (`Draft` — an MTP companion the backend tags from its `MTP-` quant
// marker) and any installed chat model small enough to draft for a bigger one (the `draft-simple` mode's usual setup).
// Cloud entries are excluded — a drafter must be a local file the supervisor can pass to `--spec-model`.
export function toDraftModelOptions(models: LocalModelDto[], nodeAvailable: boolean): ModelOption[] {
	return models
		.filter((model) => (model.kind === "Draft" || model.kind === "Chat") && !CLOUD_PROVIDERS.has(model.provider ?? ""))
		.map((model) => toModelOption(model, nodeAvailable));
}

// The concrete installed model the runtime will actually resolve for the synthetic "Local default" selection.
// Mirrors backend LocalDefaultChatModelResolver: the node default (isSelected) when it is an installed chat model,
// else the fallback `OrderByDescending(modifiedAtUtc).ThenBy(modelName)` pick (newest modified first, then name
// ascending). `modifiedAtUtc` is an epoch number on the DTO, so it is compared numerically (a missing value sorts
// oldest). Returns undefined when no installed chat-capable local model exists.
export function resolveLocalDefaultModel(models: LocalModelDto[]): LocalModelDto | undefined {
	const chatModels = models.filter(isLocalChatModel);
	return (
		chatModels.find((model) => model.isSelected) ??
		chatModels.toSorted((a, b) => {
			// Mirror backend fallback: newest modified first, then name ascending. Treat a missing modifiedAtUtc as
			// oldest (-Infinity) so it sorts last under the descending order.
			const am = a.modifiedAtUtc ?? Number.NEGATIVE_INFINITY;
			const bm = b.modifiedAtUtc ?? Number.NEGATIVE_INFINITY;
			if (am !== bm) {
				return bm - am; // descending by modifiedAtUtc
			}
			return (a.modelName ?? "").localeCompare(b.modelName ?? "");
		})[0]
	);
}

// The resolved local-default model's NAME, for callers that need the concrete model the backend will run when the
// "Local default" sentinel is selected (e.g. the model-details poll feeding the context-usage meter). Unlike the
// store's selectedModelName/configuredDefaultModelName — which may name a model whose GGUF was never downloaded —
// this only ever names an INSTALLED model, matching what the backend resolver actually executes.
export function resolveLocalDefaultModelName(models: LocalModelDto[]): string | undefined {
	const resolved = resolveLocalDefaultModel(models);
	const name = resolved?.modelName ?? "";
	return name.length > 0 ? name : undefined;
}

// Capabilities the synthetic "Local default" composer option should advertise, derived from the concrete model the
// runtime will actually resolve (see resolveLocalDefaultModel). Picking "Local default" then offers the exact same
// reasoning/tool controls as picking that concrete model directly. Coalesces the optional generated booleans to false.
export function resolveLocalDefaultModelCapabilities(models: LocalModelDto[]): {
	isReasoningModel: boolean;
	isNativeReasoningModel: boolean;
	isToolCapable: boolean;
} {
	const resolved = resolveLocalDefaultModel(models);
	return {
		isReasoningModel: resolved?.isReasoningCapable ?? false,
		isNativeReasoningModel: resolved?.isNativeReasoningCapable ?? false,
		isToolCapable: resolved?.isToolCapable ?? false,
	};
}

// True when the composer's local model list has resolved to nothing but the synthetic "Local default" entry —
// i.e. no installed chat-capable GGUF model exists on the node. Shared by ModelSelectorCard (explains an
// otherwise-bare picker) and the chat page (pre-empts the first-send ModelNotInstalled failure with inline
// guidance). Takes the already-built `modelOptions` list (not the raw DTOs) so both call sites derive
// from the same array Chat.tsx already computes.
export function hasNoLocalChatModels(modelOptions: ModelOption[]): boolean {
	return modelOptions.every((option) => option.value === localDefaultModelValue);
}

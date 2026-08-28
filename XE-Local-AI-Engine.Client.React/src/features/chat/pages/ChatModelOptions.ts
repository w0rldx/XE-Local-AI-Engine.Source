import type { XeLocalAiEngineClientEndpointsLocalModelsV1LocalModelResponse } from "@/core/api/generated";
import { EXTERNAL_PROVIDER } from "@/core/models/LocalModelProviders";
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
		// Left UNDEFINED when the backend reports null, which is every provider but the external one: null means "not
		// declared", and coalescing it to false would silently demote every graded local model to the binary control.
		isReasoningEffortCapable: model.isReasoningEffortCapable ?? undefined,
		isToolCapable: model.isToolCapable ?? false,
		// Vision projector (mmproj) capability — drives whether the composer offers image attachments for this model.
		isMultimodal: model.isMultimodalCapable ?? false,
		isAvailable: nodeAvailable,
		statusLabel: statusLabel.length > 0 ? statusLabel : undefined,
		// Carry the serving runtime so the page can gate the model-details poll per provider.
		provider: model.provider ?? undefined,
		// The operator-authored friendly name, when the backend has one (Azure deployments and external models).
		// Data only: the picker already prefers `displayName` over `label` where it renders one.
		displayName: model.displayLabel ?? undefined,
	};
}

// Cloud-provider tags carried on list entries that render in the separate cloud sections of the picker
// (via useCloudModelOptions), not the local list. Excluded here so they appear once, in their cloud group.
const CLOUD_PROVIDERS = new Set(["CodexOAuth", "AzureFoundry"]);

function isExternalModel(model: LocalModelDto | undefined): boolean {
	return model?.provider === EXTERNAL_PROVIDER;
}

// The node's OWN chat models. External models are excluded whatever their declared locality: they are served by a
// remote endpoint over HTTP, so none of what this list feeds them — speculative drafting, the synthetic local default,
// the model-details poll — applies. They reach the picker through their own per-connection sections instead.
export function isLocalChatModel(model: LocalModelDto | undefined): model is LocalModelDto {
	return model?.kind === "Chat" && !CLOUD_PROVIDERS.has(model.provider ?? "") && !isExternalModel(model);
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
// Cloud and external entries are excluded — a drafter must be a local file the supervisor can pass to `--spec-model`,
// and an external model is an HTTP endpoint, not a file on this node.
export function toDraftModelOptions(models: LocalModelDto[], nodeAvailable: boolean): ModelOption[] {
	return models
		.filter(
			(model) =>
				(model.kind === "Draft" || model.kind === "Chat") &&
				!CLOUD_PROVIDERS.has(model.provider ?? "") &&
				!isExternalModel(model),
		)
		.map((model) => toModelOption(model, nodeAvailable));
}

// Chat options for the operator-registered external endpoints, one per registered model. They carry their connection
// identity and declared trust so the picker can group them per connection and label the right egress cue.
//
// `isCloud` is deliberately NOT set, even for a declared-cloud connection: that flag selects the Codex Responses effort
// vocabulary (minimal/xhigh), which this feature explicitly does not offer. The declared locality travels on its own
// field instead, and the reasoning controls follow the model's declared capabilities like any other model's.
export function toExternalModelOptions(models: LocalModelDto[], nodeAvailable: boolean): ModelOption[] {
	return models
		.filter((model) => isExternalModel(model) && model.kind === "Chat")
		.map((model) => ({
			...toModelOption(model, nodeAvailable),
			externalConnectionId: model.externalConnectionId ?? undefined,
			externalConnectionName: model.externalConnectionName ?? undefined,
			declaredLocality: model.declaredLocality ?? undefined,
		}));
}

// One picker section per external connection, in the order the connections first appear in the catalog. Grouping lives
// here rather than in the component so the sections are unit-testable and the component stays a renderer.
export interface ExternalModelGroup {
	connectionId: string;
	connectionName: string;
	// True when the connection is declared Cloud — or when its locality is missing/unrecognized, matching the
	// backend's fail-closed direction: the more privileged "local" label is only shown for a positive declaration.
	isDeclaredCloud: boolean;
	items: ModelOption[];
}

export function groupExternalModelOptions(options: ModelOption[]): ExternalModelGroup[] {
	const groups = new Map<string, ExternalModelGroup>();
	for (const option of options) {
		const connectionId = option.externalConnectionId ?? "";
		const existing = groups.get(connectionId);
		if (existing) {
			existing.items.push(option);
			continue;
		}
		groups.set(connectionId, {
			connectionId,
			connectionName: option.externalConnectionName ?? connectionId,
			isDeclaredCloud: option.declaredLocality !== "local",
			items: [option],
		});
	}
	return [...groups.values()];
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
	isMultimodal: boolean;
} {
	const resolved = resolveLocalDefaultModel(models);
	return {
		isReasoningModel: resolved?.isReasoningCapable ?? false,
		isNativeReasoningModel: resolved?.isNativeReasoningCapable ?? false,
		isToolCapable: resolved?.isToolCapable ?? false,
		isMultimodal: resolved?.isMultimodalCapable ?? false,
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

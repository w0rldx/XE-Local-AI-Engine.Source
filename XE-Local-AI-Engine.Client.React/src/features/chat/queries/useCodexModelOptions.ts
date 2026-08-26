// Derives the list of cloud chat model options shown in the chat picker's cloud sections.
//
// Cloud models come from the unified GET /api/local/v1/models response. Items where
// `provider === "CodexOAuth"` are Codex entries injected when the user is signed into ChatGPT;
// items where `provider === "AzureFoundry"` are deployments from a saved Azure Foundry connection.
// Each is tagged with its provider so ModelSelectorCard can render one labeled group per provider.
// When neither is configured the backend omits them, so this hook returns an empty array and the
// cloud sections are hidden from the picker.
//
// This uses no separate endpoint and has no status-query dependency.

import { useQuery } from "@tanstack/react-query";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import type { ModelOption } from "@/features/chat/models/ChatModels";

export const CODEX_PROVIDER = "CodexOAuth";
export const AZURE_FOUNDRY_PROVIDER = "AzureFoundry";

// Capability flags as the BACKEND reports them for a cloud entry. These used to be hard-coded `true` here, which made
// the picker advertise capabilities the runtime then denied: the Azure mapper sets IsReasoningCapable = false, yet
// every Azure deployment still rendered the violet "Reasoning" pill AND — because isCloud is true — got the full
// six-level Codex effort menu (none/minimal/low/medium/high/xhigh), all of it inert. The effort is not merely ignored;
// it is structurally unreachable, because InvocationAgentFactory only writes the reasoning side-channel inside
// `if (definition.SupportsThinking)`. Read the DTO instead of asserting — the backend is the authority for both flags.
interface CloudModelCapabilities {
	readonly isReasoningCapable?: boolean;
	readonly isToolCapable?: boolean;
}

export function toCloudModelOption(modelName: string, capabilities?: CloudModelCapabilities): ModelOption {
	return {
		value: modelName,
		label: modelName,
		displayName: modelName,
		// Codex models support the full OpenAI Responses reasoning.effort vocabulary (none/minimal/low/medium/high/xhigh);
		// the backend reports IsReasoningCapable = true for them. Defaulting to true keeps the previous behaviour when a
		// caller supplies no DTO.
		isReasoningModel: capabilities?.isReasoningCapable ?? true,
		// Codex (gpt-5.x / OpenAI Responses) supports function tools; the backend capability gate
		// (CodexProviderCapabilities.SupportsToolCalling) is the authoritative runtime guard.
		isToolCapable: capabilities?.isToolCapable ?? true,
		isAvailable: true,
		isCloud: true,
		provider: CODEX_PROVIDER,
	};
}

export function toAzureFoundryModelOption(deploymentName: string, capabilities?: CloudModelCapabilities): ModelOption {
	return {
		value: deploymentName,
		label: deploymentName,
		displayName: deploymentName,
		// The backend reports IsReasoningCapable = false for Azure deployments today. Default to false so the picker
		// cannot advertise a reasoning control the pipeline never reads.
		isReasoningModel: capabilities?.isReasoningCapable ?? false,
		isToolCapable: capabilities?.isToolCapable ?? true,
		isAvailable: true,
		isCloud: true,
		provider: AZURE_FOUNDRY_PROVIDER,
	};
}

/**
 * Returns cloud (Codex + Azure Foundry) ModelOptions for the chat picker, derived from the unified
 * /models list. Empty array when no cloud entries are present (signed out / no Azure connection).
 */
export function useCodexModelOptions(): ModelOption[] {
	const modelsQuery = useQuery({
		...withResponseValidation(listLocalModelsOptions()),
		// Reuse the same cached value as the local model list — 30s is fine for the picker.
		staleTime: 30_000,
	});

	const items = modelsQuery.data?.items ?? [];
	return items
		.map((item) => {
			const name = item.modelName ?? "";
			if (item.provider === CODEX_PROVIDER) {
				return toCloudModelOption(name, item);
			}
			if (item.provider === AZURE_FOUNDRY_PROVIDER) {
				return toAzureFoundryModelOption(name, item);
			}
			return undefined;
		})
		.filter((option): option is ModelOption => option !== undefined && option.value.length > 0);
}

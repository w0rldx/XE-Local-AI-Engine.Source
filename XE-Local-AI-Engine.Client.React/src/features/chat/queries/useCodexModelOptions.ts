// Derives the list of cloud chat model options shown in the chat picker's cloud sections.
//
// Cloud models come from the unified GET /api/local/v1/models response. Items where
// `provider === "CodexOAuth"` are Codex entries injected when the user is signed into ChatGPT;
// items where `provider === "AzureFoundry"` are deployments from a saved Azure Foundry connection.
// Each is tagged with its provider so ModelSelectorCard can render one labeled group per provider.
// When neither is configured the backend omits them, so this hook returns an empty array and the
// cloud sections are hidden from the picker.
//
// This is the T2-seam implementation: no separate endpoint, no status-query dependency.

import { useQuery } from "@tanstack/react-query";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import type { ModelOption } from "@/features/chat/models/ChatModels";

export const CODEX_PROVIDER = "CodexOAuth";
export const AZURE_FOUNDRY_PROVIDER = "AzureFoundry";

export function toCloudModelOption(modelName: string): ModelOption {
	return {
		value: modelName,
		label: modelName,
		displayName: modelName,
		// Codex models support the full OpenAI Responses reasoning.effort vocabulary (none/minimal/low/medium/high/xhigh).
		isReasoningModel: true,
		// Codex (gpt-5.x / OpenAI Responses) supports function tools; the backend capability gate
		// (CodexProviderCapabilities.SupportsToolCalling) is the authoritative runtime guard.
		isToolCapable: true,
		isAvailable: true,
		isCloud: true,
		provider: CODEX_PROVIDER,
	};
}

export function toAzureFoundryModelOption(deploymentName: string): ModelOption {
	return {
		value: deploymentName,
		label: deploymentName,
		displayName: deploymentName,
		// Azure deployments stream through the OpenAI Responses pipeline; the backend capability gate is
		// authoritative for the actual deployment.
		isReasoningModel: true,
		isToolCapable: true,
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
				return toCloudModelOption(name);
			}
			if (item.provider === AZURE_FOUNDRY_PROVIDER) {
				return toAzureFoundryModelOption(name);
			}
			return undefined;
		})
		.filter((option): option is ModelOption => option !== undefined && option.value.length > 0);
}

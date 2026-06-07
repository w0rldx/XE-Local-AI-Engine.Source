// Derives the list of Codex cloud model options available in the chat picker.
//
// Cloud models come from the unified GET /api/local/v1/models response — items where
// `provider === "CodexOAuth"` are cloud entries injected by the backend when the user is
// signed in. When signed out the backend omits them, so this hook returns an empty array
// and the cloud section is hidden from the picker.
//
// This is the T2-seam implementation: no separate endpoint, no status-query dependency.

import { useQuery } from "@tanstack/react-query";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import type { ModelOption } from "@/features/chat/models/ChatModels";

const CODEX_PROVIDER = "CodexOAuth";

function toCloudModelOption(modelName: string): ModelOption {
	return {
		value: modelName,
		label: modelName,
		displayName: modelName,
		// Codex models support the full OpenAI Responses reasoning.effort vocabulary (none/minimal/low/medium/high/xhigh).
		isReasoningModel: true,
		isToolCapable: false,
		isAvailable: true,
		isCloud: true,
	};
}

/**
 * Returns cloud (Codex) ModelOptions for the chat picker, derived from the unified
 * /models list. Empty array when no CodexOAuth entries are present (signed out or
 * backend not configured).
 */
export function useCodexModelOptions(): ModelOption[] {
	const modelsQuery = useQuery({
		...withResponseValidation(listLocalModelsOptions()),
		// Reuse the same cached value as the local model list — 30s is fine for the picker.
		staleTime: 30_000,
	});

	const items = modelsQuery.data?.items ?? [];
	return items
		.filter((item) => item.provider === CODEX_PROVIDER)
		.map((item) => toCloudModelOption(item.modelName ?? ""))
		.filter((option) => option.value.length > 0);
}

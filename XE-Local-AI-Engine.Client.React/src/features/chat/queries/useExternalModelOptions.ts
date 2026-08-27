// Derives the chat-picker options for the operator-registered external OpenAI-compatible endpoints.
//
// They arrive on the same unified GET /api/local/v1/models response as everything else, tagged with the `external`
// provider plus the connection they belong to and its declared Local/Cloud trust. They are deliberately kept out of
// the LOCAL option list (see ChatModelOptions' D10 containment): a model served over HTTP by another process is never
// a speculative draft model and never the synthetic local default, so it reaches the picker through its own
// per-connection sections instead.
//
// Empty array when no connection is registered, which hides those sections entirely.

import { useQuery } from "@tanstack/react-query";

import { listLocalModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import type { ModelOption } from "@/features/chat/models/ChatModels";
import { toExternalModelOptions } from "@/features/chat/pages/ChatModelOptions";

export function useExternalModelOptions(): ModelOption[] {
	const modelsQuery = useQuery({
		...withResponseValidation(listLocalModelsOptions()),
		// Same cached value as the local model list and the cloud options — 30s is fine for the picker.
		staleTime: 30_000,
	});

	// An external endpoint is reachable independently of the local runtime, so these options do not inherit the
	// node's own availability flag the way the local list does.
	return toExternalModelOptions(modelsQuery.data?.items ?? [], true);
}

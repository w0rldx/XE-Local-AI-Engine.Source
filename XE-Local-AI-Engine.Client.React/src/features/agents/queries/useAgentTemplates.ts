import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	importAgentTemplatesMutation,
	listAgentTemplatesOptions,
	listAgentTemplatesQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { agentDefinitionsInvalidationKey, agentDefinitionsQueryIds } from "@/features/agents/queries/useAgentDefinitions";

// Server state for the starter-agent gallery. Reads use the generated hey-api `listAgentTemplatesOptions()` (which
// wires the shared axios instance + TanStack Query AbortSignal automatically), wrapped in withResponseValidation so a
// zod response-shape failure surfaces as an ApiError (never a raw ZodError). The import mutation invalidates BOTH the
// agent-definitions list (so newly-seeded rows appear on the agents surface) AND the templates list (so each summary's
// `alreadyImported` flag refreshes and the gallery disables it).

// Returns the array of template summaries (the generated response wraps them in `{ items?: [...] }`).
export function useAgentTemplates() {
	return useQuery({
		...withResponseValidation(listAgentTemplatesOptions()),
		select: (data) => data.items ?? [],
	});
}

// Import keeps the page-facing `{ body: { slugs } }` envelope the gallery passes and dispatches it through the generated
// mutation, then invalidates the definitions list (partial `_id` match, reusing the definitions hook's helper) and the
// templates list (generated query key) on success so both surfaces refetch.
export function useImportAgentTemplates() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(importAgentTemplatesMutation()),
		onSuccess: () =>
			Promise.all([
				// Newly-seeded agents are ordinary definitions — refetch the definitions list so they appear.
				queryClient.invalidateQueries({
					queryKey: agentDefinitionsInvalidationKey(agentDefinitionsQueryIds.list),
				}),
				// Refetch the templates list so each summary's `alreadyImported` flag (and the disabled checkbox) updates.
				queryClient.invalidateQueries({ queryKey: listAgentTemplatesQueryKey() }),
			]),
	});
}

import { useMutation, useQueryClient } from "@tanstack/react-query";

import { importKnowledgeRepositoryMutation } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import type { KnowledgeRepositoryImportResult } from "@/features/knowledge/models/KnowledgeModels";
import { knowledgeInvalidationKey, knowledgeQueryIds } from "@/features/knowledge/queries/useKnowledgeDocuments";

export interface KnowledgeRepositoryImportCallbacks {
	readonly onSuccess?: (result: KnowledgeRepositoryImportResult) => void;
	readonly onError?: (error: unknown) => void;
}

/** Imports a registered repository into one collection, then refreshes every cached document-list variant. */
export function useKnowledgeRepositoryImport(callbacks: KnowledgeRepositoryImportCallbacks = {}) {
	const queryClient = useQueryClient();
	return useMutation({
		...withResponseValidation(importKnowledgeRepositoryMutation()),
		onSuccess: async (result) => {
			await queryClient.invalidateQueries({ queryKey: knowledgeInvalidationKey(knowledgeQueryIds.listDocuments) });
			callbacks.onSuccess?.(result);
		},
		onError: (error) => callbacks.onError?.(error),
	});
}

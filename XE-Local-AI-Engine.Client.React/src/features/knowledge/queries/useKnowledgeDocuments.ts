import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";

import {
	deleteKnowledgeDocumentMutation,
	getKnowledgeDocumentOptions,
	listKnowledgeDocumentsOptions,
	reindexCorpusMutation,
	reindexKnowledgeDocumentMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import type { KnowledgeDocument, KnowledgeDocumentDetail } from "@/features/knowledge/models/KnowledgeModels";

// Server state for the knowledge-base surface. Reads run through the generated hey-api `*Options()` (which wire the
// shared axios instance + TanStack Query AbortSignal automatically) wrapped in withResponseValidation so a zod
// response-shape failure surfaces as an ApiError (never a raw ZodError). Mutations invalidate by the generated
// query key's `_id` discriminator (partial-object match), so every cached variant of an endpoint refetches. The
// SignalR hub (useKnowledgeBaseHub) layers live invalidation on top for server-pushed indexing transitions —
// TanStack Query stays the authoritative source. Upload lives in useKnowledgeUpload (raw multipart) and search in
// useKnowledgeSearch (mutation) so this module owns list/detail reads + lifecycle mutations only.

// The generated query keys are single-element arrays `[{ _id: "<operationId>", ... }]`. Invalidating with just the
// `_id` partial object matches every cached variant of that endpoint. The operationIds equal the generated SDK fn
// names. Centralized here (and reused by useKnowledgeBaseHub) so the literal `_id` key — which trips biome's
// naming-convention rule — is constructed in exactly one place.
export const knowledgeQueryIds = {
	listDocuments: "listKnowledgeDocuments",
	getDocument: "getKnowledgeDocument",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one KB endpoint. */
export function knowledgeInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

/**
 * The full document list (indexing status + metadata). Items are the authoritative server rows. `enabled` gates the
 * fetch for consumers that must not hit the endpoint until the knowledge-base surface is active (e.g. the chat composer,
 * which only needs the list to decide whether the "Use Knowledge Base" toggle has indexed docs to search); it defaults
 * to true so the knowledge-base page keeps fetching unconditionally.
 */
export function useKnowledgeDocuments(enabled = true) {
	return useQuery({
		...withResponseValidation(listKnowledgeDocumentsOptions()),
		select: (data): readonly KnowledgeDocument[] => data.items ?? [],
		enabled,
	});
}

/** One document's detail + chunks. Disabled until a document id is supplied and the drawer is open. */
export function useKnowledgeDocumentDetail(documentId: string, enabled: boolean) {
	return useQuery({
		...withResponseValidation(getKnowledgeDocumentOptions({ path: { documentId: documentId || "" } })),
		select: (data): KnowledgeDocumentDetail => data,
		enabled: enabled && documentId.length > 0,
	});
}

export interface KnowledgeMutationCallbacks {
	readonly onSuccess?: () => void;
	readonly onError?: (error: unknown) => void;
}

// Invalidates both the list and every open document detail after a lifecycle mutation, so the row status and the
// (possibly open) drawer both refetch canonical state.
function useInvalidateKnowledge(): () => Promise<void> {
	const queryClient = useQueryClient();
	return useCallback(
		() =>
			Promise.all([
				queryClient.invalidateQueries({ queryKey: knowledgeInvalidationKey(knowledgeQueryIds.listDocuments) }),
				queryClient.invalidateQueries({ queryKey: knowledgeInvalidationKey(knowledgeQueryIds.getDocument) }),
			]).then(() => undefined),
		[queryClient],
	);
}

/** Deletes a document (chunks + embeddings removed server-side), then invalidates the list + details. */
export function useDeleteKnowledgeDocument(callbacks: KnowledgeMutationCallbacks = {}) {
	const invalidate = useInvalidateKnowledge();
	return useMutation({
		...withResponseValidation(deleteKnowledgeDocumentMutation()),
		onSuccess: async () => {
			await invalidate();
			callbacks.onSuccess?.();
		},
		onError: (error) => callbacks.onError?.(error),
	});
}

/** Re-runs extraction/embedding/indexing for a single document (e.g. after an embedding-model change). */
export function useReindexKnowledgeDocument(callbacks: KnowledgeMutationCallbacks = {}) {
	const invalidate = useInvalidateKnowledge();
	return useMutation({
		...withResponseValidation(reindexKnowledgeDocumentMutation()),
		onSuccess: async () => {
			await invalidate();
			callbacks.onSuccess?.();
		},
		onError: (error) => callbacks.onError?.(error),
	});
}

export interface ReindexCorpusCallbacks {
	readonly onSuccess?: (enqueuedCount: number) => void;
	readonly onError?: (error: unknown) => void;
}

/** Re-indexes every stale document in the corpus. Returns the enqueued count so the caller can report it. */
export function useReindexKnowledgeCorpus(callbacks: ReindexCorpusCallbacks = {}) {
	const invalidate = useInvalidateKnowledge();
	return useMutation({
		...withResponseValidation(reindexCorpusMutation()),
		onSuccess: async (response) => {
			await invalidate();
			callbacks.onSuccess?.(response.enqueuedCount);
		},
		onError: (error) => callbacks.onError?.(error),
	});
}

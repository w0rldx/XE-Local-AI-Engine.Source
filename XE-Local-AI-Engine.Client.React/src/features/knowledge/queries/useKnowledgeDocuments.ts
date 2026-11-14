import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";

import type { ListKnowledgeDocumentsResponse } from "@/core/api/generated";
import {
	deleteKnowledgeDocumentMutation,
	getKnowledgeDocumentOptions,
	listKnowledgeCollectionsOptions,
	listKnowledgeDocumentsOptions,
	reindexCorpusMutation,
	reindexKnowledgeDocumentMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { keepPreviousPageOf } from "@/core/ui/components/TablePagination/useTablePagination";
import type { KnowledgeDocumentDetail } from "@/features/knowledge/models/KnowledgeModels";

// Server state for the knowledge-base surface. Reads run through the generated hey-api `*Options()` (which wire the
// shared axios instance + TanStack Query AbortSignal automatically). Mutations invalidate by the generated
// query key's `_id` discriminator (partial-object match), so every cached variant of an endpoint refetches. The
// SignalR hub (useKnowledgeBaseHub) layers live invalidation on top for server-pushed indexing transitions —
// TanStack Query stays the authoritative source. Upload lives in useKnowledgeUpload (raw multipart) and search in
// useKnowledgeSearch (mutation) so this module owns list/detail reads + lifecycle mutations only.

// Generated keys are object arrays; TanStack partial matching on `_id` invalidates every endpoint variant.
export const knowledgeQueryIds = {
	listDocuments: "listKnowledgeDocuments",
	getDocument: "getKnowledgeDocument",
	listCollections: "listKnowledgeCollections",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one KB endpoint. */
export function knowledgeInvalidationKey(operationId: string): readonly [{ _id: string }] {
	return [{ _id: operationId }];
}

/**
 * One page of the document list, newest first, plus the facts about the whole collection a page alone cannot answer
 * (`totalCount`, `hasStaleDocuments`). The server pages it: a repository import can hold tens of thousands of
 * documents, and every row costs a decrypted name.
 */
export function useKnowledgeDocumentPage(collectionId: string, limit: number, offset: number) {
	const query = { collectionId, limit, offset };
	return useQuery({
		...listKnowledgeDocumentsOptions({ query }),
		// The next page has no data yet: the previous page of the same collection keeps `totalCount`, so the pager does
		// not clamp back to page 1 mid-request. Another collection's rows are never shown in its place.
		placeholderData: keepPreviousPageOf(query),
	});
}

// The flags ride the envelope, so a one-row read answers them; the availability and toggle reads share this key.
function oneRowOptions(collectionId: string | undefined) {
	return listKnowledgeDocumentsOptions({ query: { ...(collectionId ? { collectionId } : {}), limit: 1 } });
}

/**
 * Whether the collection (every collection when none is named) holds anything a search can find — for the chat
 * composer's "Use Knowledge Base" toggle.
 */
export function useKnowledgeHasSearchableDocuments(enabled: boolean, collectionId?: string) {
	return useQuery({
		...oneRowOptions(collectionId),
		select: (data): boolean => data.hasSearchableDocuments,
		enabled,
	});
}

const noDocumentNames: ReadonlyMap<string, string> = new Map();

function toDocumentNames(data: ListKnowledgeDocumentsResponse): ReadonlyMap<string, string> {
	return new Map(data.items.map((document) => [document.documentId, document.displayName]));
}

/**
 * The decrypted display names of exactly these documents (empty until they load), for labelling search hits and chat
 * sources: a hit's own title is the storage reference, and its document may sit on any page of the list.
 */
export function useKnowledgeDocumentNames(documentIds: readonly string[]): ReadonlyMap<string, string> {
	// 200 is the server's ceiling for an id set (`ListKnowledgeDocumentsRequestValidator.MaxLimit`).
	const ids = [...new Set(documentIds)].sort().slice(0, 200);
	return (
		useQuery({
			...listKnowledgeDocumentsOptions({ query: { documentIds: ids, limit: Math.max(ids.length, 1) } }),
			select: toDocumentNames,
			enabled: ids.length > 0,
		}).data ?? noDocumentNames
	);
}

/**
 * The collection ids that hold documents, DEFAULT first (the server always lists it). Shared by the chat composer's
 * collection picker and the knowledge page's collection box; `enabled` gates the chat fetch like the document list.
 */
export function useKnowledgeCollections(enabled = true) {
	return useQuery({
		...listKnowledgeCollectionsOptions(),
		select: (data): readonly string[] => data.items.map((item) => item.collectionId),
		enabled,
	});
}

/**
 * Whether this node can actually embed a new upload: the list endpoint reports the embedding-model resolution the
 * ingestion lane itself runs, so the upload gate and the background embedder agree. It asks for ONE row, because the
 * verdict rides the envelope and the rows are never read. While no model is resolved it polls, because a finished GGUF
 * download invalidates only the installed-model list — a different key — and the gate must clear without a page reload.
 */
export function useKnowledgeEmbeddingAvailability(collectionId?: string) {
	return useQuery({
		...oneRowOptions(collectionId),
		select: (data): boolean => data.embeddingModelAvailable,
		refetchInterval: (query) => (query.state.data?.embeddingModelAvailable === false ? 10_000 : false),
	});
}

/** One document's detail + chunks. Disabled until a document id is supplied and the drawer is open. */
export function useKnowledgeDocumentDetail(documentId: string, enabled: boolean) {
	return useQuery({
		...getKnowledgeDocumentOptions({ path: { documentId: documentId || "" } }),
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
				queryClient.invalidateQueries({ queryKey: knowledgeInvalidationKey(knowledgeQueryIds.listCollections) }),
			]).then(() => undefined),
		[queryClient],
	);
}

/** Deletes a document (chunks + embeddings removed server-side), then invalidates the list + details. */
export function useDeleteKnowledgeDocument(callbacks: KnowledgeMutationCallbacks = {}) {
	const invalidate = useInvalidateKnowledge();
	return useMutation({
		...deleteKnowledgeDocumentMutation(),
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
		...reindexKnowledgeDocumentMutation(),
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
		...reindexCorpusMutation(),
		onSuccess: async (response) => {
			await invalidate();
			callbacks.onSuccess?.(response.enqueuedCount);
		},
		onError: (error) => callbacks.onError?.(error),
	});
}

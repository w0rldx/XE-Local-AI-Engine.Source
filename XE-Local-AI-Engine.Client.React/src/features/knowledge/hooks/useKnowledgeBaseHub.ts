import { useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { knowledgeInvalidationKey, knowledgeQueryIds } from "@/features/knowledge/queries/useKnowledgeDocuments";
import type { KnowledgeDocument, KnowledgeDocumentStatus } from "@/features/knowledge/models/KnowledgeModels";

// Server-pushed knowledge-base events. Each push carries the document id + its new indexing status. The handler
// applies that status to the cached list row IMMEDIATELY (optimistic), then invalidates so the remaining fields
// (chunk count, failure reason) refetch canonical state. The optimistic write matters because a fast transition
// (e.g. an instant Pending→Extracting→Failed when the embedding model is unavailable) fires several pushes in the
// same tick; a pure invalidate can let an in-flight refetch settle on a stale intermediate snapshot, leaving the
// row visually stuck. Writing the pushed status directly guarantees the terminal status is never lost. The event
// name is the string method name the backend invokes on the client.
const DOCUMENT_CHANGED = "knowledge.documentChanged";

/** Payload of the {@link DOCUMENT_CHANGED} push: which document changed and its new status. */
interface KnowledgeDocumentChangedEvent {
	readonly documentId: string;
	readonly status: KnowledgeDocumentStatus;
}

/** The cached shape of the list query (the raw response before the `select` extracts `items`). */
interface KnowledgeDocumentListCache {
	readonly items?: readonly KnowledgeDocument[];
}

// Subscribes to the knowledge-base SignalR hub for the lifetime of the mounting component. On any document change
// (a Pending→Extracting→…→Indexed/Failed transition, or an add/delete) the document list AND every open document
// detail are invalidated so the table status pills and the detail drawer refresh without a manual reload. The hub
// is a best-effort live channel only — authoritative state is always refetched via TanStack Query, so a connection
// failure is tolerated silently (logged to console.warn) and the queries still serve their last good data. Mirrors
// useSchedulerHub's lifecycle (auto-reconnect, start/stop race handling).
export function useKnowledgeBaseHub(): void {
	const queryClient = useQueryClient();

	useEffect(() => {
		// Shared refcounted connection: reused across mounts so re-opening the knowledge-base page does not pay a fresh
		// negotiate + WebSocket upgrade. The DOCUMENT_CHANGED handler below stays per-mount so this subscriber coexists
		// with any other subscriber to the same hub.
		const hub = acquireHubConnection("knowledge-base/hub");
		const { connection } = hub;

		const invalidateDocuments = (): void => {
			queryClient
				.invalidateQueries({ queryKey: knowledgeInvalidationKey(knowledgeQueryIds.listDocuments) })
				.catch(() => undefined);
			queryClient
				.invalidateQueries({ queryKey: knowledgeInvalidationKey(knowledgeQueryIds.getDocument) })
				.catch(() => undefined);
		};

		const applyDocumentChanged = (event: KnowledgeDocumentChangedEvent | undefined): void => {
			// Optimistically stamp the pushed status onto the matching cached row so a rapid multi-transition burst
			// cannot leave the row stuck on a stale intermediate status while the refetch is still in flight.
			if (event && typeof event.documentId === "string" && typeof event.status === "string") {
				queryClient.setQueriesData<KnowledgeDocumentListCache>(
					{ queryKey: knowledgeInvalidationKey(knowledgeQueryIds.listDocuments) },
					(old) =>
						old
							? {
									...old,
									items: old.items?.map((item) =>
										item.documentId === event.documentId ? { ...item, status: event.status } : item,
									),
								}
							: old,
				);
			}

			invalidateDocuments();
		};

		connection.on(DOCUMENT_CHANGED, applyDocumentChanged);

		return () => {
			connection.off(DOCUMENT_CHANGED, applyDocumentChanged);
			// Release the shared lease: the manager stops the connection only after the LAST subscriber releases, and only
			// once the start promise settles (so cleanup never aborts an in-flight negotiation under StrictMode / fast remounts).
			hub.release();
		};
	}, [queryClient]);
}

import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";

import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
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
		const connection = new HubConnectionBuilder()
			.withUrl(buildLocalApiUrl("knowledge-base/hub"), {
				accessTokenFactory: () => useNodeAuthStore.getState().accessToken ?? "",
			})
			// Persistent notification channel (mounted for the page lifetime), so auto-reconnect after a transient drop —
			// otherwise live invalidation is silently lost for the rest of the session.
			.withAutomaticReconnect()
			.configureLogging(LogLevel.Warning)
			.build();

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

		let disposed = false;
		const startPromise = connection.start().catch((error: unknown) => {
			// A start aborted by our own cleanup (StrictMode double-invoke / fast remount) is not a real failure.
			if (disposed) {
				return;
			}
			// A hub that cannot connect must not break the page — TanStack Query still serves cached state.
			console.warn("knowledge-base hub failed to start", error);
		});

		return () => {
			disposed = true;
			connection.off(DOCUMENT_CHANGED, applyDocumentChanged);
			// Stop only AFTER start settles so cleanup never aborts an in-flight negotiation (the "stopped during
			// negotiation" race that left the hub permanently disconnected under StrictMode / fast remounts).
			startPromise.finally(() => {
				connection.stop().catch((error: unknown) => {
					console.warn("knowledge-base hub failed to stop", error);
				});
			});
		};
	}, [queryClient]);
}

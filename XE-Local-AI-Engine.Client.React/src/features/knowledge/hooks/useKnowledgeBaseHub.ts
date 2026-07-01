import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";

import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { knowledgeInvalidationKey, knowledgeQueryIds } from "@/features/knowledge/queries/useKnowledgeDocuments";

// Server-pushed knowledge-base events. The hub is notification-only: a push tells the client that a document's
// indexing status changed but carries no authoritative payload to render directly, so the handler simply
// invalidates the matching TanStack Query caches and lets them refetch the canonical state. The event name is the
// string method name the backend invokes on the client.
const DOCUMENT_CHANGED = "knowledge.documentChanged";

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

		connection.on(DOCUMENT_CHANGED, invalidateDocuments);

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
			connection.off(DOCUMENT_CHANGED, invalidateDocuments);
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

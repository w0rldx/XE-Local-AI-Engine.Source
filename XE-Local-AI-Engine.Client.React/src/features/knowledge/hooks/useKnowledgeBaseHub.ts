import { useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";

import { operationInvalidationKey } from "@/core/api/QueryKeys";
import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import {
	type KnowledgeDocument,
	type KnowledgeDocumentStatus,
	knowledgeStatusDescriptor,
} from "@/features/knowledge/models/KnowledgeModels";
import { knowledgeQueryIds } from "@/features/knowledge/queries/useKnowledgeDocuments";

// Server-pushed knowledge-base events. Each push carries the document id + its new indexing status. The handler
// applies that status to the cached list row IMMEDIATELY (optimistic), then invalidates so the remaining fields
// (chunk count, failure reason) refetch canonical state. The optimistic write matters because a fast transition
// (e.g. an instant Pending→Extracting→Failed when the embedding model is unavailable) fires several pushes in the
// same tick; a pure invalidate can let an in-flight refetch settle on a stale intermediate snapshot, leaving the
// row visually stuck. Writing the pushed status directly guarantees the terminal status is never lost. The event
// name is the string method name the backend invokes on the client.
const DOCUMENT_CHANGED = "knowledge.documentChanged";
const CLOSED_HUB_POLL_MS = 10_000;
// One document fires several pushes, and a repository import fires them for thousands of documents; every invalidation
// refetches each list variant (decrypting every row's name). The optimistic write shows each status at once, so the
// canonical refetch runs at most once per this window: the first push opens it, later pushes ride along, and a push
// after it fired opens the next one, so a long import still refetches steadily and its last push is always covered.
const PUSH_REFETCH_WINDOW_MS = 250;

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
				.invalidateQueries({ queryKey: operationInvalidationKey(knowledgeQueryIds.listDocuments) })
				.catch(() => undefined);
			queryClient.invalidateQueries({ queryKey: operationInvalidationKey(knowledgeQueryIds.getDocument) }).catch(() => undefined);
			// An upload into a new collection, or a delete emptying one, changes the collection list too.
			queryClient
				.invalidateQueries({ queryKey: operationInvalidationKey(knowledgeQueryIds.listCollections) })
				.catch(() => undefined);
		};

		let pushRefetch: ReturnType<typeof setTimeout> | undefined;
		const flushPushRefetch = (): void => {
			if (pushRefetch !== undefined) {
				clearTimeout(pushRefetch);
				pushRefetch = undefined;
				invalidateDocuments();
			}
		};
		const schedulePushRefetch = (): void => {
			pushRefetch ??= setTimeout(flushPushRefetch, PUSH_REFETCH_WINDOW_MS);
		};

		const applyDocumentChanged = (event: KnowledgeDocumentChangedEvent | undefined): void => {
			// Optimistically stamp the pushed status onto the matching cached row so a rapid multi-transition burst
			// cannot leave the row stuck on a stale intermediate status while the refetch is still in flight.
			if (event && typeof event.documentId === "string" && typeof event.status === "string") {
				queryClient.setQueriesData<KnowledgeDocumentListCache>(
					{ queryKey: operationInvalidationKey(knowledgeQueryIds.listDocuments) },
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

			schedulePushRefetch();
		};

		connection.on(DOCUMENT_CHANGED, applyDocumentChanged);

		// Hub's own fallback: the hub has no replay, so a reconnect re-reads what changed in the gap, and while it is
		// closed (until the shared manager's restart reports back) a still-indexing row is polled instead of freezing.
		let closedPoll: ReturnType<typeof setInterval> | undefined;
		const stopClosedPoll = (): void => {
			if (closedPoll !== undefined) {
				clearInterval(closedPoll);
				closedPoll = undefined;
			}
		};
		const hasInProgressRow = (): boolean =>
			queryClient
				.getQueriesData<KnowledgeDocumentListCache>({ queryKey: operationInvalidationKey(knowledgeQueryIds.listDocuments) })
				.some(([, data]) => data?.items?.some((item) => knowledgeStatusDescriptor(item.status)?.inProgress === true) === true);
		// release() drops these registrations.
		hub.onReconnected(() => {
			stopClosedPoll();
			invalidateDocuments();
		});
		hub.onClosed(() => {
			stopClosedPoll();
			closedPoll = setInterval(() => {
				if (hasInProgressRow()) {
					invalidateDocuments();
				}
			}, CLOSED_HUB_POLL_MS);
		});

		return () => {
			stopClosedPoll();
			connection.off(DOCUMENT_CHANGED, applyDocumentChanged);
			// A burst still waiting for its refetch leaves the cache invalidated rather than holding pre-push data as fresh.
			flushPushRefetch();
			// Release the shared lease: the manager stops the connection only after the LAST subscriber releases, and only
			// once the start promise settles (so cleanup never aborts an in-flight negotiation under StrictMode / fast remounts).
			hub.release();
		};
	}, [queryClient]);
}

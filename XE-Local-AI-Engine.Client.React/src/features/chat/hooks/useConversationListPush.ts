import type { HubConnection } from "@microsoft/signalr";
import { useQueryClient } from "@tanstack/react-query";
import { useEffect } from "react";

import { nodeChatQueryKeys } from "@/core/chat/queries/NodeChatQueryKeys";
import { nodeChatConnection } from "@/features/chat/api/NodeChatConnection";
import { inFlightAssistantMessageId } from "@/features/chat/models/ChatConversationDerivations";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

// The method name LocalChatHubEvents.ConversationsChanged pushes after a rename, pin, archive, delete or import through
// the API, and once per finished turn.
export const CONVERSATIONS_CHANGED = "chat.conversationsChanged";

/**
 * Refreshes the conversation list when the node reports a conversation change made elsewhere (another client, or the
 * API directly), so an open sidebar shows a rename or a new turn without a reload. The handler attaches to whichever
 * connection comes up, also one a later send starts after this mount's own connect failed. The hub has no replay, so the
 * list refreshes once whenever the connection comes up after a window without the handler: a mount onto a connection
 * that was not yet open (the list GET races the start), a failed connect, or a drop. A mount onto an open connection
 * does not refresh.
 */
export function useConversationListPush(): void {
	const queryClient = useQueryClient();

	useEffect(() => {
		const refresh = (event?: { conversationId?: string | null }): void => {
			queryClient.invalidateQueries({ queryKey: nodeChatQueryKeys.conversationLists() }).catch(() => undefined);
			// The open chat's row shows its cached detail over the list summary, so that detail must re-read too. `exact`
			// keeps its files and context-state children out; a detail nothing observes is only marked stale. A detail
			// holding a live turn is left alone: a refetch landing after the optimistic write of the next send would replace
			// it, and the stream that owns the turn re-reads the detail when it ends.
			const cached = event?.conversationId
				? queryClient.getQueryData<ChatConversationModel>(nodeChatQueryKeys.conversation(event.conversationId))
				: undefined;
			if (event?.conversationId && (cached === undefined || inFlightAssistantMessageId(cached) === undefined)) {
				queryClient
					.invalidateQueries({ queryKey: nodeChatQueryKeys.conversation(event.conversationId), exact: true })
					.catch(() => undefined);
			}
		};
		let disposed = false;
		let attached: HubConnection | undefined;
		// True while pushes may have been missed: the connection was not open at mount, or it dropped since.
		let missed = nodeChatConnection.status !== "connected";
		// Called from both the "connected" status and ensureConnection resolving; the first clears `missed`, so one refresh.
		const connected = (connection: HubConnection | undefined): void => {
			if (disposed || !connection) {
				return;
			}
			if (attached !== connection) {
				attached?.off(CONVERSATIONS_CHANGED, refresh);
				connection.on(CONVERSATIONS_CHANGED, refresh);
				attached = connection;
			}
			if (missed) {
				missed = false;
				refresh();
			}
		};

		nodeChatConnection
			.ensureConnection()
			.then(connected)
			.catch(() => {
				missed = true;
			});
		const unsubscribe = nodeChatConnection.subscribe({
			// "connected" fires for a first start, a reconnect and a later start (a send) after a failed connect alike.
			onStatusChange: (status) => {
				if (status === "connected") {
					connected(nodeChatConnection.current());
				} else if (status === "reconnecting" || status === "disconnected") {
					missed = true;
				}
			},
		});

		return () => {
			disposed = true;
			attached?.off(CONVERSATIONS_CHANGED, refresh);
			unsubscribe();
		};
	}, [queryClient]);
}

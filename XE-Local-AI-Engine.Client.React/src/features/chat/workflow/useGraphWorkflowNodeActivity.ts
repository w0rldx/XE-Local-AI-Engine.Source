import { HubConnectionState, type ISubscription } from "@microsoft/signalr";
import { useEffect, useState } from "react";

import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";
import { applyNodeChatStreamEvent } from "@/features/chat/api/NodeChatStreamState";
import type { ChatConversationModel, ChatStreamingState } from "@/features/chat/models/ChatModels";
import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";

// The run hub's path (`useGraphWorkflowRunHub`), so this shares its connection. Kept as a literal here rather than
// imported: the hook needs the chat fold, so it lives in chat, and importing the constant would add a cross-feature pair.
const HUB_PATH = "graph-workflows/hub";
const STREAM_METHOD = "StreamNodeActivity";
// The server asks for a resync. Mid-stream (a queue overflow) a re-opened stream starts with a fresh snapshot; as the
// FIRST frame it is the replay cap (the snapshot is too large to replay) and the server ends the stream, so re-opening
// would only earn the same answer forever. The reason is not on the wire, so the position is what tells them apart.
const RECONCILE_EVENT = "assistant-reconcile";

export interface GraphWorkflowNodeActivity {
	/**
	 * `unavailable` is the server refusing the stream (the node is no longer running, or its invocation just finished):
	 * "nothing live", never an error to show. `ended` is the terminal frame.
	 */
	readonly status: "idle" | "connecting" | "live" | "ended" | "unavailable";
	/** The folded turn: reasoning, content, phase and token counts, exactly as the chat folds its own stream. */
	readonly stream?: ChatStreamingState;
	/** The model the server resolved for this turn, off the frames' `model` (the fold keeps it on the message, not here). */
	readonly model?: string;
}

const idle: GraphWorkflowNodeActivity = { status: "idle" };
const emptyConversation: ChatConversationModel = { id: "", title: "", createdAt: "", updatedAt: "", messages: [] };

/**
 * The live output of one running Agent / LLM Call node, streamed over `graph-workflows/hub` from the same resume
 * registry the chat's `ResumeMessage` reads: a snapshot, offset deltas, then the terminal frame. Keyed by
 * `invocationId`: a new invocation (a retry, a steer) tears the subscription down and opens a fresh one, and
 * `undefined` is idle. A reconnect re-opens the stream; its snapshot re-syncs the folded text.
 */
export function useGraphWorkflowNodeActivity(
	runId: string | undefined,
	nodeKey: string | undefined,
	invocationId: string | undefined,
): GraphWorkflowNodeActivity {
	const [activity, setActivity] = useState<GraphWorkflowNodeActivity>(idle);

	useEffect(() => {
		if (!(runId && nodeKey && invocationId)) {
			setActivity(idle);
			return;
		}

		const hub = acquireHubConnection(HUB_PATH);
		const { connection } = hub;
		let disposed = false;
		let ended = false;
		let subscription: ISubscription<NodeChatStreamEventDto> | undefined;
		let conversation = emptyConversation;
		let stream: ChatStreamingState | undefined;
		let model: string | undefined;
		setActivity({ status: "connecting" });

		const open = (): void => {
			if (disposed || ended || connection.state !== HubConnectionState.Connected) {
				return;
			}
			subscription?.dispose();
			// Per subscription: whether it has folded a frame yet. A reconcile before any is the replay cap.
			let folded = false;
			subscription = connection.stream<NodeChatStreamEventDto>(STREAM_METHOD, runId, nodeKey).subscribe({
				next: (event) => {
					if (disposed) {
						return;
					}
					if (event.type === RECONCILE_EVENT) {
						if (folded) {
							open();
						} else {
							// Nothing to show live; the node's durable output is the fallback once it settles.
							ended = true;
							setActivity({ status: "unavailable", stream, model });
						}
						return;
					}
					folded = true;
					model = event.model ?? model;
					const applied = applyNodeChatStreamEvent(conversation, event, stream);
					conversation = applied.conversation;
					stream = applied.streamingMessage;
					ended = applied.isTerminal;
					setActivity({ status: ended ? "ended" : "live", stream, model });
				},
				error: () => {
					subscription = undefined;
					// SignalR errors every open stream on a transport drop BEFORE it leaves `Connected` (the reconnect starts
					// right after), so the state is read a microtask later: a drop has moved on by then and `onReconnected`
					// re-opens; a server refusal (HubException) leaves it `Connected` — "nothing live".
					queueMicrotask(() => {
						if (!(disposed || ended) && connection.state === HubConnectionState.Connected) {
							ended = true;
							setActivity({ status: "unavailable", stream, model });
						}
					});
				},
				complete: () => {
					subscription = undefined;
					if (!(disposed || ended)) {
						ended = true;
						setActivity({ status: "ended", stream, model });
					}
				},
			});
		};

		const removeReconnected = hub.onReconnected(open);
		hub.whenStarted.then(open).catch(() => undefined);

		return () => {
			disposed = true;
			subscription?.dispose();
			removeReconnected();
			hub.release();
		};
	}, [runId, nodeKey, invocationId]);

	return activity;
}

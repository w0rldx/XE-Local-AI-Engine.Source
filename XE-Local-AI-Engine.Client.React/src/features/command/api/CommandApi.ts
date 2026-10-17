import { HubConnectionState } from "@microsoft/signalr";

import { ApiError } from "@/core/api/errors/ApiError";
import {
	createNodeChatConversation,
	type XeLocalAiEngineClientEndpointsGraphWorkflowsV1GraphWorkflowIntentResponse as GraphWorkflowIntentResponse,
	invokeGraphWorkflowIntent,
} from "@/core/api/generated";
import type { ConflictProblemDetails } from "@/core/api/models/ProblemDetails";
import { acquireHubConnection } from "@/core/api/signalr/SharedHubConnection";

// The two calls behind Enter on the command page: the intent endpoint, and the default agent the page falls through to
// when nothing matched. The agent reply rides the chat hub's `SendMessage` stream directly rather than the chat
// feature's adapter: the page shows one reply line, so it needs none of the adapter's resume, revision or tool-card
// machinery, and importing it would be a cross-feature edge.

/** `NodeConflictProblemType.GraphWorkflowIntentExpired`, serialized as the enum name. */
const intentExpiredConflictType = "GraphWorkflowIntentExpired";
const chatHubPath = "chat/hub";
const terminalChatEvents = new Set(["assistant-completed", "assistant-cancelled", "assistant-failed", "assistant-interrupted"]);

/** Why the intent call gave no answer the page can act on. */
export type IntentRefusal = "expired" | "unavailable" | "failed";

export function invokeIntent(utterance: string, requestId: string, utteredAt: string): Promise<GraphWorkflowIntentResponse> {
	return invokeGraphWorkflowIntent({ body: { utterance, requestId, utteredAt } }).then(({ data }) => data);
}

export function toIntentRefusal(error: unknown): IntentRefusal {
	if (!(error instanceof ApiError)) {
		return "failed";
	}
	if (
		error.statusCode === 409 &&
		(error.apiProblemDetails as Partial<ConflictProblemDetails>).conflictType === intentExpiredConflictType
	) {
		return "expired";
	}
	// The node's Graph Workflows switch answers every graph route with 404.
	return error.statusCode === 404 ? "unavailable" : "failed";
}

/** Opens a new conversation for the utterance; the title follows the chat page's own 48-character rule. */
export async function createAgentConversation(utterance: string): Promise<string> {
	const normalized = utterance.replace(/\s+/g, " ").trim();
	const title = normalized.length > 48 ? `${normalized.slice(0, 45)}…` : normalized;
	const { data } = await createNodeChatConversation({ body: { title } });
	return data.conversationId;
}

interface ChatStreamFrame {
	readonly type: string;
	readonly delta?: string | null;
	readonly content?: string | null;
}

/**
 * Sends the utterance to the default agent (no agent id, no model: the node resolves both) and reports the reply text as
 * it grows. Resolves once the turn ends; rejects when it failed or the hub is down.
 */
export async function streamAgentReply(conversationId: string, content: string, onText: (text: string) => void): Promise<void> {
	const hub = acquireHubConnection(chatHubPath);
	try {
		await hub.whenStarted;
		if (hub.connection.state !== HubConnectionState.Connected) {
			throw new Error("the chat hub is not connected");
		}
		await new Promise<void>((resolve, reject) => {
			let text = "";
			hub.connection.stream<ChatStreamFrame>("SendMessage", { conversationId, content }).subscribe({
				next: (frame) => {
					// Deliberate simplification: deltas are appended without the offset check the chat page's guard does; the terminal frame
					// carries the full text and replaces whatever was assembled, so a dropped frame only shows until then.
					if (frame.type === "assistant-delta" && frame.delta) {
						text += frame.delta;
					} else if (
						(frame.type === "assistant-snapshot" || terminalChatEvents.has(frame.type)) &&
						typeof frame.content === "string"
					) {
						text = frame.content;
					}
					onText(text);
					if (frame.type === "assistant-failed") {
						reject(new Error("the agent turn failed"));
					}
				},
				error: reject,
				complete: resolve,
			});
		});
	} finally {
		hub.release();
	}
}

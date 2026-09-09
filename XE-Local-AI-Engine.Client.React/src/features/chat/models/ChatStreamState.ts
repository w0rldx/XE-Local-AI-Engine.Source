import type { ChatConversationModel, ChatStreamingState, ChatTimelineEntry } from "@/features/chat/models/ChatModels";

/** The single in-flight stream the chat page owns, and the handle that cancels it. */
export interface ActiveChatStream {
	conversationId: string;
	messageId: string;
	requestId: string;
	abortController: AbortController;
}

// One rAF-batched streaming commit (see commitStreamState / useStreamCommitScheduler).
export interface PendingStreamCommit {
	conversation: ChatConversationModel;
	// Terminal frames also refresh the conversation-LIST cache; per-token frames update only the detail cache.
	writeConversationList: boolean;
	streamingMessage: ChatStreamingState;
	// Tool-lifecycle entries seen since the last flush; empty on plain token deltas.
	toolTimelineEntries: ChatTimelineEntry[];
}

import { describe, expect, it } from "vitest";

import type { NodeChatStreamEventDto } from "@/features/chat/api/NodeChatApi";
import {
	appendOptimisticNodeChatSend,
	applyNodeChatStreamEvent,
	markNodeChatStreamTerminated,
	nodeChatStreamEventTypes,
} from "@/features/chat/api/NodeChatStreamState";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

const conversation: ChatConversationModel = {
	id: "conversation-1",
	title: "Local chat",
	createdAt: "2026-05-24T00:00:00.000Z",
	updatedAt: "2026-05-24T00:00:00.000Z",
	messages: [],
};

function streamEvent(overrides: Partial<NodeChatStreamEventDto> = {}): NodeChatStreamEventDto {
	return {
		type: nodeChatStreamEventTypes.assistantDelta,
		conversationId: "conversation-1",
		messageId: "assistant-1",
		requestId: "request-1",
		status: "streaming",
		sequence: 3,
		occurredAtUtc: 1_700_000_001_000,
		delta: "he",
		content: "he",
		...overrides,
	};
}

describe("node chat stream state", () => {
	it("adds optimistic user and assistant messages before the SignalR stream starts", () => {
		const updated = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
			"local-default",
		);

		expect(updated.messages).toMatchObject([
			{ id: "user-1", role: "user", content: "hello", status: "completed", sortOrder: 1 },
			{ id: "assistant-1", role: "assistant", content: "", status: "pending", sortOrder: 2, model: "local-default" },
		]);
	});

	it("applies partial and terminal assistant stream events to the correlated message only", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
			"local-default",
		);

		const partial = applyNodeChatStreamEvent(optimistic, streamEvent({ content: "hello back", delta: "hello back" }));
		const terminal = applyNodeChatStreamEvent(
			partial.conversation,
			streamEvent({ type: nodeChatStreamEventTypes.assistantCompleted, status: "completed", content: "hello back", delta: null, inputTokens: 10, outputTokens: 3, totalTokens: 13 }),
		);

		expect(partial.streamingMessage).toMatchObject({ messageId: "assistant-1", content: "hello back", isActive: true });
		expect(terminal.streamingMessage).toMatchObject({ messageId: "assistant-1", content: "hello back", isActive: false, totalTokens: 13 });
		expect(terminal.conversation.messages).toMatchObject([
			{ id: "user-1", role: "user", content: "hello", status: "completed" },
			{ id: "assistant-1", role: "assistant", content: "hello back", status: "completed", inputTokens: 10, outputTokens: 3, totalTokens: 13 },
		]);
	});

	it("marks the assistant turn queued, then clears the queued flag when streaming begins", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
		);

		const queued = applyNodeChatStreamEvent(
			optimistic,
			// Mirrors the runner's wire shape: type "assistant-queued", status "queued" (NodeChatMessageStatusValues.Queued).
			streamEvent({ type: nodeChatStreamEventTypes.assistantQueued, status: "queued", content: null, delta: null }),
		);
		expect(queued.streamingMessage).toMatchObject({ messageId: "assistant-1", isQueued: true, isActive: true, content: "" });
		expect(queued.isTerminal).toBe(false);
		expect(queued.conversation.messages.find((message) => message.id === "assistant-1")?.status).toBe("queued");

		const streaming = applyNodeChatStreamEvent(
			queued.conversation,
			streamEvent({ type: nodeChatStreamEventTypes.assistantStreaming, status: "streaming", content: "hi", delta: "hi" }),
		);
		expect(streaming.streamingMessage).toMatchObject({ messageId: "assistant-1", isQueued: false, isActive: true, content: "hi" });
		expect(streaming.conversation.messages.find((message) => message.id === "assistant-1")?.status).toBe("streaming");
	});

	it("stamps the streaming state with the assistant turn's own start time, not the conversation update time", () => {
		// Optimistic assistant turn started at 00:00:01; a later conversation update must not be borrowed.
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
		);
		const conversationWithLaterUpdate = { ...optimistic, updatedAt: "2026-05-24T09:30:00.000Z" };

		const persisted = applyNodeChatStreamEvent(
			conversationWithLaterUpdate,
			streamEvent({ type: nodeChatStreamEventTypes.userMessagePersisted, status: "completed", content: "hello" }),
		);
		const streaming = applyNodeChatStreamEvent(persisted.conversation, streamEvent({ content: "hi", delta: "hi" }));

		expect(persisted.streamingMessage.startedAt).toBe("2026-05-24T00:00:01.000Z");
		expect(streaming.streamingMessage.startedAt).toBe("2026-05-24T00:00:01.000Z");
	});

	it("does not let a user-persisted event clobber the optimistic assistant placeholder", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
		);

		const applied = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({ type: nodeChatStreamEventTypes.userMessagePersisted, status: "completed", content: "hello" }),
		);

		expect(applied.conversation.messages).toMatchObject([
			{ id: "user-1", role: "user", content: "hello", status: "completed" },
			{ id: "assistant-1", role: "assistant", content: "", status: "pending" },
		]);
	});

	it("marks only the active assistant message as cancelled during local cancellation", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
		);

		const cancelled = markNodeChatStreamTerminated(optimistic, "assistant-1", "cancelled");

		expect(cancelled.streamingMessage).toMatchObject({ messageId: "assistant-1", isActive: false });
		expect(cancelled.conversation.messages).toMatchObject([
			{ id: "user-1", role: "user", status: "completed" },
			{ id: "assistant-1", role: "assistant", status: "cancelled" },
		]);
	});
});

import { describe, expect, it } from "vitest";

import type { NodeChatStreamEventDto } from "@/features/chat/api/NodeChatApi";
import {
	accumulateToolTimelineEntry,
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

	it("turns a tool-call-requested event into a timeline entry without clobbering assistant content", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
		);
		const streaming = applyNodeChatStreamEvent(optimistic, streamEvent({ content: "partial", delta: "partial" }));

		const requested = applyNodeChatStreamEvent(
			streaming.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallRequested,
				toolCallId: "call-1",
				toolName: "search_docs",
				arguments: '{"q":"x"}',
				requiresApproval: false,
				content: null,
				delta: null,
			}),
		);

		expect(requested.timelineEntry).toMatchObject({
			id: "call-1",
			messageId: "assistant-1",
			type: "ToolCall",
			toolName: "search_docs",
			toolArgs: '{"q":"x"}',
			state: "requesting",
			requiresApproval: false,
		});
		// The tool event leaves the assistant message content/status untouched and keeps the turn live.
		expect(requested.conversation.messages.find((message) => message.id === "assistant-1")).toMatchObject({ content: "partial", status: "streaming" });
		expect(requested.streamingMessage).toMatchObject({ messageId: "assistant-1", content: "partial", isActive: true });
		expect(requested.isTerminal).toBe(false);
	});

	it("carries the approval flag onto a tool-call-requested timeline entry", () => {
		const requested = applyNodeChatStreamEvent(
			conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallRequested,
				toolCallId: "call-9",
				toolName: "delete_file",
				requiresApproval: true,
				content: null,
				delta: null,
			}),
		);

		expect(requested.timelineEntry).toMatchObject({ id: "call-9", state: "waiting", requiresApproval: true });
	});

	it("transitions the requested entry to received on tool-call-completed, keyed by tool call id", () => {
		const requested = applyNodeChatStreamEvent(
			conversation,
			streamEvent({ type: nodeChatStreamEventTypes.toolCallRequested, toolCallId: "call-1", toolName: "search_docs", content: null, delta: null }),
		);
		const completed = applyNodeChatStreamEvent(
			conversation,
			streamEvent({ type: nodeChatStreamEventTypes.toolCallCompleted, toolCallId: "call-1", toolName: "search_docs", result: "3 results", isError: false, content: null, delta: null }),
		);

		const requestedEntry = requested.timelineEntry;
		const completedEntry = completed.timelineEntry;
		if (!requestedEntry || !completedEntry) {
			throw new Error("expected tool timeline entries");
		}

		const accumulated = accumulateToolTimelineEntry(accumulateToolTimelineEntry([], requestedEntry), completedEntry);

		// The completed event collapses onto the requested entry (same id) instead of appending a duplicate.
		expect(accumulated).toHaveLength(1);
		expect(accumulated[0]).toMatchObject({ id: "call-1", type: "ToolResult", state: "received", toolResult: "3 results" });
	});

	it("preserves the requiresApproval flag when the completed entry merges over the requested entry", () => {
		const requested = applyNodeChatStreamEvent(
			conversation,
			streamEvent({ type: nodeChatStreamEventTypes.toolCallRequested, toolCallId: "call-1", toolName: "delete_file", requiresApproval: true, content: null, delta: null }),
		);
		const completed = applyNodeChatStreamEvent(
			conversation,
			streamEvent({ type: nodeChatStreamEventTypes.toolCallCompleted, toolCallId: "call-1", toolName: "delete_file", result: "ok", isError: false, content: null, delta: null }),
		);

		const requestedEntry = requested.timelineEntry;
		const completedEntry = completed.timelineEntry;
		if (!requestedEntry || !completedEntry) {
			throw new Error("expected tool timeline entries");
		}

		// The completed tool-event omits requiresApproval; the merge must keep the requested entry's flag rather
		// than letting the object-spread clobber it to undefined.
		expect(completedEntry.requiresApproval).toBeUndefined();
		const accumulated = accumulateToolTimelineEntry(accumulateToolTimelineEntry([], requestedEntry), completedEntry);

		expect(accumulated).toHaveLength(1);
		expect(accumulated[0]).toMatchObject({ id: "call-1", type: "ToolResult", state: "received", requiresApproval: true });
	});

	it("maps a failed tool-call-completed event to a failed timeline entry", () => {
		const failed = applyNodeChatStreamEvent(
			conversation,
			streamEvent({ type: nodeChatStreamEventTypes.toolCallCompleted, toolCallId: "call-1", toolName: "search_docs", result: "boom", isError: true, content: null, delta: null }),
		);

		expect(failed.timelineEntry).toMatchObject({ id: "call-1", state: "failed", toolResult: "boom" });
	});

	it("builds ordered parts for reasoning → tool → reasoning, splitting a new Thoughts block after the tool", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"what time is it",
			"2026-05-24T00:00:01.000Z",
		);

		// Reasoning before the tool (seq 3) opens the first segment.
		const firstReasoning = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({ sequence: 3, content: null, delta: null, reasoningDelta: "let me check the clock" }),
		);
		// Tool requested (seq 4) then completed (seq 5) collapse onto one tool part.
		const toolRequested = applyNodeChatStreamEvent(
			firstReasoning.conversation,
			streamEvent({ type: nodeChatStreamEventTypes.toolCallRequested, sequence: 4, toolCallId: "call-1", toolName: "get_time", arguments: "{}", content: null, delta: null }),
		);
		const toolCompleted = applyNodeChatStreamEvent(
			toolRequested.conversation,
			streamEvent({ type: nodeChatStreamEventTypes.toolCallCompleted, sequence: 5, toolCallId: "call-1", toolName: "get_time", result: "12:00", isError: false, content: null, delta: null }),
		);
		// Reasoning after the tool (seq 6) must OPEN A NEW segment (Option A second Thoughts block).
		const secondReasoning = applyNodeChatStreamEvent(
			toolCompleted.conversation,
			streamEvent({ sequence: 6, content: null, delta: null, reasoningDelta: "the tool says noon" }),
		);

		const parts = secondReasoning.streamingMessage.parts;
		if (!parts) {
			throw new Error("expected ordered parts on the streaming state");
		}

		expect(parts.map((part) => part.kind)).toEqual(["reasoning", "tool", "reasoning"]);
		expect(parts[0]).toMatchObject({ kind: "reasoning", text: "let me check the clock" });
		expect(parts[1]).toMatchObject({ kind: "tool", id: "call-1", name: "get_time", state: "received", args: "{}", result: "12:00" });
		expect(parts[2]).toMatchObject({ kind: "reasoning", text: "the tool says noon" });
	});

	it("appends consecutive reasoning deltas into the same trailing segment", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hi",
			"2026-05-24T00:00:01.000Z",
		);

		const first = applyNodeChatStreamEvent(optimistic, streamEvent({ sequence: 3, content: null, delta: null, reasoningDelta: "think" }));
		const second = applyNodeChatStreamEvent(first.conversation, streamEvent({ sequence: 4, content: null, delta: null, reasoningDelta: "ing more" }));

		const parts = second.streamingMessage.parts ?? [];
		expect(parts).toHaveLength(1);
		expect(parts[0]).toMatchObject({ kind: "reasoning", text: "thinking more" });
	});

	it("does not duplicate a tool part when a completed event repeats for the same tool-call id (guards vercel/ai#6342)", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"go",
			"2026-05-24T00:00:01.000Z",
		);

		const requested = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({ type: nodeChatStreamEventTypes.toolCallRequested, sequence: 3, toolCallId: "call-1", toolName: "get_time", content: null, delta: null }),
		);
		const completed = applyNodeChatStreamEvent(
			requested.conversation,
			streamEvent({ type: nodeChatStreamEventTypes.toolCallCompleted, sequence: 4, toolCallId: "call-1", toolName: "get_time", result: "12:00", isError: false, content: null, delta: null }),
		);
		// A second completed event for the same id (e.g. a resume replay) must collapse onto the same part.
		const completedAgain = applyNodeChatStreamEvent(
			completed.conversation,
			streamEvent({ type: nodeChatStreamEventTypes.toolCallCompleted, sequence: 5, toolCallId: "call-1", toolName: "get_time", result: "12:00", isError: false, content: null, delta: null }),
		);

		const toolParts = (completedAgain.streamingMessage.parts ?? []).filter((part) => part.kind === "tool");
		expect(toolParts).toHaveLength(1);
		expect(toolParts[0]).toMatchObject({ kind: "tool", id: "call-1", state: "received", result: "12:00" });
	});

	it("preserves a prior text part when a new reasoning delta arrives (text parts survive re-decomposition)", () => {
		// Seed a conversation message whose parts already contain a text segment (simulates a resumed/re-attached turn).
		const withTextPart = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"go",
			"2026-05-24T00:00:01.000Z",
		);
		// Inject a text part directly into the cached assistant message so the reducer reads it as prior state.
		const seeded = {
			...withTextPart,
			messages: withTextPart.messages.map((message) =>
				message.id === "assistant-1"
					? {
							...message,
							parts: [{ kind: "text" as const, id: "assistant-1:1", sequence: 1, text: "narration" }],
						}
					: message,
			),
		};

		// A new reasoning delta arrives after the text part: it should open a new reasoning segment, and the text
		// part must survive in the resulting parts[] rather than being silently dropped.
		const result = applyNodeChatStreamEvent(
			seeded,
			streamEvent({ sequence: 2, content: null, delta: null, reasoningDelta: "thinking" }),
		);

		const parts = result.streamingMessage.parts ?? [];
		const textParts = parts.filter((part) => part.kind === "text");
		const reasoningParts = parts.filter((part) => part.kind === "reasoning");
		expect(textParts).toHaveLength(1);
		expect(textParts[0]).toMatchObject({ kind: "text", text: "narration" });
		expect(reasoningParts).toHaveLength(1);
		expect(reasoningParts[0]).toMatchObject({ kind: "reasoning", text: "thinking" });
		// Text (seq 1) precedes reasoning (seq 2) in the sorted output.
		expect(parts.map((part) => part.kind)).toEqual(["text", "reasoning"]);
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

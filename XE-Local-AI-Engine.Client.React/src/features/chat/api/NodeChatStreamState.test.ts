import { describe, expect, it } from "vitest";

import {
	accumulateToolTimelineEntry,
	appendOptimisticNodeChatSend,
	applyNodeChatStreamEvent,
	markNodeChatStreamTerminated,
	nodeChatStreamEventTypes,
} from "@/features/chat/api/NodeChatStreamState";
import type { ChatConversationModel, ChatToolPart } from "@/features/chat/models/ChatModels";
import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";

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

function reloadedConversationWithTools(parts: ChatToolPart[] = []): ChatConversationModel {
	return {
		...conversation,
		messages: [
			{
				id: "assistant-1",
				conversationId: conversation.id,
				role: "assistant",
				content: "",
				status: "streaming",
				createdAt: "2026-05-24T00:00:01.000Z",
				sortOrder: 1,
				parts,
			},
		],
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
			streamEvent({
				type: nodeChatStreamEventTypes.assistantCompleted,
				status: "completed",
				content: "hello back",
				delta: null,
				inputTokens: 10,
				outputTokens: 3,
				totalTokens: 13,
			}),
		);

		expect(partial.streamingMessage).toMatchObject({ messageId: "assistant-1", content: "hello back", isActive: true });
		expect(terminal.streamingMessage).toMatchObject({
			messageId: "assistant-1",
			content: "hello back",
			isActive: false,
			totalTokens: 13,
		});
		expect(terminal.conversation.messages).toMatchObject([
			{ id: "user-1", role: "user", content: "hello", status: "completed" },
			{
				id: "assistant-1",
				role: "assistant",
				content: "hello back",
				status: "completed",
				inputTokens: 10,
				outputTokens: 3,
				totalTokens: 13,
			},
		]);
	});

	it("appends the delta and ignores a stale content field on an assistant-delta", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
		);

		const first = applyNodeChatStreamEvent(optimistic, streamEvent({ sequence: 1, delta: "he", content: null, contentOffset: 0 }));
		// The direct regression test for the delta-only wire contract: a delta must NEVER read `content`. A
		// server that still stamped the accumulated text there — or a stale replay of it — would clobber the
		// accumulation instead of extending it, and reading it at all is what made every frame re-send the
		// whole message.
		const second = applyNodeChatStreamEvent(
			first.conversation,
			streamEvent({ sequence: 2, delta: "llo", content: "stale snapshot", contentOffset: 2 }),
		);

		expect(second.streamingMessage.content).toBe("hello");
		expect(second.conversation.messages.find((message) => message.id === "assistant-1")?.content).toBe("hello");
	});

	it("replaces the accumulated content wholesale on an assistant-snapshot and keeps the turn live", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
		);

		const partial = applyNodeChatStreamEvent(optimistic, streamEvent({ sequence: 1, delta: "he", content: null, contentOffset: 0 }));
		// A resume/gap/overflow repair replaces the client's text rather than extending it — the server is the
		// authority on what the turn actually contains at this point.
		const snapshot = applyNodeChatStreamEvent(
			partial.conversation,
			streamEvent({ type: nodeChatStreamEventTypes.assistantSnapshot, sequence: 2, delta: null, content: "hello there" }),
		);

		expect(snapshot.streamingMessage).toMatchObject({ content: "hello there", isActive: true });
		// A snapshot is a mid-stream state replacement, never a terminal: the turn must stay live afterwards.
		expect(snapshot.isTerminal).toBe(false);
		expect(snapshot.conversation.messages.find((message) => message.id === "assistant-1")?.status).toBe("streaming");
	});

	it("merges reasoning by the same three rules: a delta appends, a snapshot replaces, a lifecycle event carries", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
		);

		const first = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({ sequence: 1, delta: null, content: null, reasoningDelta: "let me ", reasoningOffset: 0 }),
		);
		const second = applyNodeChatStreamEvent(
			first.conversation,
			// A stale `reasoning` snapshot on a delta is ignored exactly like a stale `content`.
			streamEvent({ sequence: 2, delta: null, content: null, reasoningDelta: "think", reasoning: "stale", reasoningOffset: 7 }),
		);
		expect(second.streamingMessage.reasoning).toBe("let me think");

		const lifecycle = applyNodeChatStreamEvent(
			second.conversation,
			streamEvent({ type: nodeChatStreamEventTypes.assistantPhase, sequence: 3, delta: null, content: null }),
		);
		expect(lifecycle.streamingMessage.reasoning).toBe("let me think");

		const snapshot = applyNodeChatStreamEvent(
			second.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.assistantSnapshot,
				sequence: 4,
				delta: null,
				content: "answer",
				reasoning: "let me think it through",
			}),
		);
		expect(snapshot.streamingMessage.reasoning).toBe("let me think it through");
	});

	it("keeps the ordered parts interleave and the accumulated content across a delta → tool → delta sequence", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"what time is it",
			"2026-05-24T00:00:01.000Z",
		);

		const firstDelta = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({ sequence: 3, delta: "chec", content: null, contentOffset: 0, reasoningDelta: "checking" }),
		);
		const tool = applyNodeChatStreamEvent(
			firstDelta.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallRequested,
				sequence: 4,
				toolCallId: "call-1",
				toolName: "get_time",
				arguments: "{}",
				content: null,
				delta: null,
			}),
		);
		const secondDelta = applyNodeChatStreamEvent(
			tool.conversation,
			streamEvent({ sequence: 5, delta: "king", content: null, contentOffset: 4, reasoningDelta: " done" }),
		);

		// Content accumulates across the tool call, and the delta-only merge leaves the parts interleave exactly
		// as before: the post-tool reasoning still opens its own segment rather than extending the pre-tool one.
		expect(secondDelta.streamingMessage.content).toBe("checking");
		expect(secondDelta.streamingMessage.parts?.map((part) => part.kind)).toEqual(["reasoning", "tool", "reasoning"]);
		expect(secondDelta.streamingMessage.parts?.[0]).toMatchObject({ kind: "reasoning", text: "checking" });
		expect(secondDelta.streamingMessage.parts?.[2]).toMatchObject({ kind: "reasoning", text: " done" });
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
			// A lifecycle event carries no text on the wire (only deltas, snapshots and terminals do), so the turn is
			// still empty here — the flag flip is the whole assertion.
			streamEvent({ type: nodeChatStreamEventTypes.assistantStreaming, status: "streaming", content: null, delta: null }),
		);
		expect(streaming.streamingMessage).toMatchObject({
			messageId: "assistant-1",
			isQueued: false,
			isActive: true,
			content: "",
		});
		expect(streaming.conversation.messages.find((message) => message.id === "assistant-1")?.status).toBe("streaming");
	});

	it("carries agent attribution through streaming events so the live turn shows the selected agent, not Default Assistant", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
			"local-default",
			"Code Reviewer",
		);
		// appendOptimisticNodeChatSend only takes agentName; stamp the id the way the send flow does so we can
		// assert it survives the stream-state rebuild too.
		const withAttribution: ChatConversationModel = {
			...optimistic,
			messages: optimistic.messages.map((message) =>
				message.id === "assistant-1" ? { ...message, agentDefinitionId: "agent-7" } : message,
			),
		};

		const queued = applyNodeChatStreamEvent(
			withAttribution,
			streamEvent({ type: nodeChatStreamEventTypes.assistantQueued, status: "queued", content: null, delta: null }),
		);
		expect(queued.conversation.messages.find((message) => message.id === "assistant-1")).toMatchObject({
			agentName: "Code Reviewer",
			agentDefinitionId: "agent-7",
		});

		const streaming = applyNodeChatStreamEvent(
			queued.conversation,
			streamEvent({ type: nodeChatStreamEventTypes.assistantStreaming, status: "streaming", content: "hi", delta: "hi" }),
		);
		expect(streaming.conversation.messages.find((message) => message.id === "assistant-1")).toMatchObject({
			agentName: "Code Reviewer",
			agentDefinitionId: "agent-7",
		});
	});

	it("keeps agent attribution on a terminal cancellation so a stopped turn still shows the selected agent", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
			"local-default",
			"Code Reviewer",
		);
		const withAttribution: ChatConversationModel = {
			...optimistic,
			messages: optimistic.messages.map((message) =>
				message.id === "assistant-1" ? { ...message, agentDefinitionId: "agent-7" } : message,
			),
		};

		const cancelled = markNodeChatStreamTerminated(withAttribution, "assistant-1", "cancelled");
		expect(cancelled.conversation.messages.find((message) => message.id === "assistant-1")).toMatchObject({
			status: "cancelled",
			agentName: "Code Reviewer",
			agentDefinitionId: "agent-7",
		});
	});

	it("stamps reasoningEffort on the optimistic assistant message so the attribution row is live during streaming", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
			"local-default",
			undefined,
			"medium",
		);

		expect(optimistic.messages.find((message) => message.id === "assistant-1")).toMatchObject({
			reasoningEffort: "medium",
		});
	});

	it("carries reasoningEffort through streaming events so the live attribution row stays consistent", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
			"local-default",
			undefined,
			"high",
		);

		const streaming = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({ type: nodeChatStreamEventTypes.assistantStreaming, status: "streaming", content: "hi", delta: "hi" }),
		);
		expect(streaming.conversation.messages.find((message) => message.id === "assistant-1")).toMatchObject({
			reasoningEffort: "high",
		});
	});

	it("carries reasoningEffort through a terminal cancelled state", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hello",
			"2026-05-24T00:00:01.000Z",
			"local-default",
			undefined,
			"none",
		);

		const cancelled = markNodeChatStreamTerminated(optimistic, "assistant-1", "cancelled");
		expect(cancelled.conversation.messages.find((message) => message.id === "assistant-1")).toMatchObject({
			reasoningEffort: "none",
		});
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
		expect(requested.conversation.messages.find((message) => message.id === "assistant-1")).toMatchObject({
			content: "partial",
			status: "streaming",
		});
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
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallRequested,
				toolCallId: "call-1",
				toolName: "search_docs",
				content: null,
				delta: null,
			}),
		);
		const completed = applyNodeChatStreamEvent(
			conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallCompleted,
				toolCallId: "call-1",
				toolName: "search_docs",
				result: "3 results",
				isError: false,
				content: null,
				delta: null,
			}),
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
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallRequested,
				toolCallId: "call-1",
				toolName: "delete_file",
				requiresApproval: true,
				content: null,
				delta: null,
			}),
		);
		const completed = applyNodeChatStreamEvent(
			conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallCompleted,
				toolCallId: "call-1",
				toolName: "delete_file",
				result: "ok",
				isError: false,
				content: null,
				delta: null,
			}),
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
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallCompleted,
				toolCallId: "call-1",
				toolName: "search_docs",
				result: "boom",
				isError: true,
				content: null,
				delta: null,
			}),
		);

		expect(failed.timelineEntry).toMatchObject({ id: "call-1", state: "failed", toolResult: "boom" });
	});

	it("flips the matching tool card to waiting-for-approval and attaches the approval request id", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"delete it",
			"2026-05-24T00:00:01.000Z",
		);
		// The approval-gated tool first surfaces its own tool-call-requested card (requiresApproval flag off there).
		const requested = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallRequested,
				toolCallId: "call-1",
				toolName: "delete_file",
				requiresApproval: false,
				content: null,
				delta: null,
			}),
		);

		const approval = applyNodeChatStreamEvent(
			requested.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.approvalRequested,
				toolCallId: "call-1",
				toolName: "delete_file",
				approvalRequestId: "approval-42",
				content: null,
				delta: null,
			}),
		);

		const toolPart = approval.streamingMessage.parts?.find((part) => part.kind === "tool");
		expect(toolPart).toMatchObject({
			kind: "tool",
			id: "call-1",
			state: "waiting",
			requiresApproval: true,
			pendingApprovalRequestId: "approval-42",
		});
		// The approval prompt never mutates content/status and never yields a timeline entry.
		expect(approval.timelineEntry).toBeUndefined();
		expect(approval.isTerminal).toBe(false);
		expect(approval.streamingMessage.isActive).toBe(true);
	});

	it("creates a waiting tool card from an approval event even when no tool-call-requested card exists yet", () => {
		const approval = applyNodeChatStreamEvent(
			conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.approvalRequested,
				toolCallId: "call-7",
				toolName: "run_shell",
				approvalRequestId: "approval-77",
				content: null,
				delta: null,
			}),
		);

		const toolPart = approval.streamingMessage.parts?.find((part) => part.kind === "tool");
		expect(toolPart).toMatchObject({
			kind: "tool",
			id: "call-7",
			name: "run_shell",
			state: "waiting",
			requiresApproval: true,
			pendingApprovalRequestId: "approval-77",
		});
	});

	it("keeps a metadata-poor approval replay actionable when no persisted tool card exists", () => {
		const approval = applyNodeChatStreamEvent(
			reloadedConversationWithTools(),
			streamEvent({
				type: nodeChatStreamEventTypes.approvalRequested,
				sequence: 8,
				toolCallId: null,
				toolName: null,
				approvalRequestId: "approval-generic",
				content: null,
				delta: null,
			}),
		);

		expect(approval.streamingMessage.parts?.filter((part) => part.kind === "tool")).toEqual([
			expect.objectContaining({
				id: "approval-generic",
				name: "tool",
				state: "waiting",
				requiresApproval: true,
				pendingApprovalRequestId: "approval-generic",
			}),
		]);
	});

	it("keeps an ambiguous metadata-poor question replay actionable without mutating either unresolved card", () => {
		const approval = applyNodeChatStreamEvent(
			reloadedConversationWithTools([
				{
					kind: "tool",
					id: "call-1",
					sequence: 4,
					name: "read_file",
					state: "waiting",
					args: '{"path":"one.txt"}',
				},
				{
					kind: "tool",
					id: "call-2",
					sequence: 5,
					name: "write_file",
					state: "requesting",
					args: '{"path":"two.txt"}',
				},
			]),
			streamEvent({
				type: nodeChatStreamEventTypes.questionRequested,
				sequence: 8,
				toolCallId: null,
				toolName: null,
				questionRequestId: "question-generic",
				questions: '[{"question":"Continue?","options":[{"label":"Yes"},{"label":"No"}]}]',
				content: null,
				delta: null,
			}),
		);

		const toolParts = approval.streamingMessage.parts?.filter((part) => part.kind === "tool");
		expect(toolParts).toHaveLength(3);
		expect(toolParts?.slice(0, 2)).toMatchObject([
			{ id: "call-1", name: "read_file", args: '{"path":"one.txt"}' },
			{ id: "call-2", name: "write_file", args: '{"path":"two.txt"}' },
		]);
		expect(toolParts?.[2]).toMatchObject({
			id: "question-generic",
			name: "tool",
			state: "waiting",
			pendingQuestion: { requestId: "question-generic", questions: [{ question: "Continue?" }] },
		});
	});

	it("removes a generic replay card when the approved tool completion arrives", () => {
		const approval = applyNodeChatStreamEvent(
			reloadedConversationWithTools(),
			streamEvent({
				type: nodeChatStreamEventTypes.approvalRequested,
				sequence: 8,
				toolCallId: null,
				toolName: null,
				approvalRequestId: "approval-generic",
				content: null,
				delta: null,
			}),
		);
		expect(approval.streamingMessage.parts?.filter((part) => part.kind === "tool")).toHaveLength(1);

		const completed = applyNodeChatStreamEvent(
			approval.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallCompleted,
				sequence: 9,
				toolCallId: "call-9",
				toolName: "delete_file",
				result: "deleted",
				isError: false,
				content: null,
				delta: null,
			}),
		);

		const toolParts = completed.streamingMessage.parts?.filter((part) => part.kind === "tool");
		expect(toolParts).toHaveLength(1);
		expect(toolParts?.[0]).toMatchObject({ id: "call-9", name: "delete_file", state: "received", result: "deleted" });
	});

	it("reattaches a metadata-poor approval replay to the persisted unresolved tool card", () => {
		const reloaded: ChatConversationModel = {
			...conversation,
			messages: [
				{
					id: "assistant-1",
					conversationId: conversation.id,
					role: "assistant",
					content: "",
					status: "streaming",
					createdAt: "2026-05-24T00:00:01.000Z",
					sortOrder: 1,
					parts: [
						{
							kind: "tool",
							id: "call-1",
							sequence: 4,
							name: "mcp__files__write_file",
							state: "waiting",
							args: '{"path":"notes.txt"}',
							requiresApproval: true,
						},
					],
				},
			],
		};

		const approval = applyNodeChatStreamEvent(
			reloaded,
			streamEvent({
				type: nodeChatStreamEventTypes.approvalRequested,
				sequence: 8,
				toolCallId: null,
				toolName: null,
				approvalRequestId: "approval-replayed",
				sessionScopeEligible: false,
				content: null,
				delta: null,
			}),
		);

		const toolParts = approval.streamingMessage.parts?.filter((part) => part.kind === "tool");
		expect(toolParts).toHaveLength(1);
		expect(toolParts?.[0]).toMatchObject({
			id: "call-1",
			name: "mcp__files__write_file",
			state: "waiting",
			args: '{"path":"notes.txt"}',
			requiresApproval: true,
			pendingApprovalRequestId: "approval-replayed",
			pendingApprovalSessionScopeEligible: false,
		});
	});

	it("does not leave a synthetic empty tool card after a rehydrated approval completes", () => {
		const reloaded: ChatConversationModel = {
			...conversation,
			messages: [
				{
					id: "assistant-1",
					conversationId: conversation.id,
					role: "assistant",
					content: "",
					status: "streaming",
					createdAt: "2026-05-24T00:00:01.000Z",
					sortOrder: 1,
					parts: [
						{
							kind: "tool",
							id: "call-1",
							sequence: 4,
							name: "delete_file",
							state: "waiting",
							args: '{"path":"notes.txt"}',
							requiresApproval: true,
						},
					],
				},
			],
		};
		const approval = applyNodeChatStreamEvent(
			reloaded,
			streamEvent({
				type: nodeChatStreamEventTypes.approvalRequested,
				sequence: 8,
				toolCallId: null,
				toolName: null,
				approvalRequestId: "approval-replayed",
				content: null,
				delta: null,
			}),
		);
		const completed = applyNodeChatStreamEvent(
			approval.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallCompleted,
				sequence: 9,
				toolCallId: "call-1",
				toolName: "delete_file",
				result: "deleted",
				isError: false,
				content: null,
				delta: null,
			}),
		);

		const toolParts = completed.streamingMessage.parts?.filter((part) => part.kind === "tool");
		expect(toolParts).toHaveLength(1);
		expect(toolParts?.[0]).toMatchObject({ id: "call-1", name: "delete_file", state: "received", result: "deleted" });
	});

	it("clears the pending approval once the approved tool completes", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"delete it",
			"2026-05-24T00:00:01.000Z",
		);
		const requested = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallRequested,
				toolCallId: "call-1",
				toolName: "delete_file",
				requiresApproval: false,
				content: null,
				delta: null,
			}),
		);
		const approval = applyNodeChatStreamEvent(
			requested.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.approvalRequested,
				toolCallId: "call-1",
				toolName: "delete_file",
				approvalRequestId: "approval-42",
				content: null,
				delta: null,
			}),
		);
		const completed = applyNodeChatStreamEvent(
			approval.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallCompleted,
				toolCallId: "call-1",
				toolName: "delete_file",
				result: "deleted",
				isError: false,
				content: null,
				delta: null,
			}),
		);

		const toolPart = completed.conversation.messages
			.find((message) => message.id === "assistant-1")
			?.parts?.find((part) => part.kind === "tool");
		expect(toolPart).toMatchObject({ id: "call-1", state: "received", result: "deleted" });
		// The pending approval is cleared once the tool resolves so the controls disappear.
		expect(toolPart && "pendingApprovalRequestId" in toolPart ? toolPart.pendingApprovalRequestId : undefined).toBeUndefined();
	});

	it("clears a lingering pending-approval waiting card when the turn terminalizes via a stream failure", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"delete it",
			"2026-05-24T00:00:01.000Z",
		);
		// Reproduce the API-tool DENY shape: the waiting card is created purely by the approval event, and no
		// tool-call-completed ever follows (the deny short-circuits the tool before it runs).
		const approval = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({
				type: nodeChatStreamEventTypes.approvalRequested,
				toolCallId: "call-deny",
				toolName: "delete_file",
				approvalRequestId: "approval-99",
				content: null,
				delta: null,
			}),
		);
		expect(approval.streamingMessage.parts?.find((part) => part.kind === "tool")).toMatchObject({
			state: "waiting",
			pendingApprovalRequestId: "approval-99",
		});

		// The deny fails the turn; the terminal event must promptly retire the dead approval prompt rather than leave
		// it live until the post-stream refetch.
		const failed = applyNodeChatStreamEvent(
			approval.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.assistantFailed,
				status: "failed",
				content: null,
				delta: null,
				error: "Tool call was rejected by the user.",
			}),
		);

		expect(failed.isTerminal).toBe(true);
		const toolPart = failed.conversation.messages
			.find((message) => message.id === "assistant-1")
			?.parts?.find((part) => part.kind === "tool");
		expect(toolPart).toMatchObject({ id: "call-deny", state: "failed" });
		expect(toolPart && "pendingApprovalRequestId" in toolPart ? toolPart.pendingApprovalRequestId : undefined).toBeUndefined();
		const streamingToolPart = failed.streamingMessage.parts?.find((part) => part.kind === "tool");
		expect(streamingToolPart && "pendingApprovalRequestId" in streamingToolPart ? streamingToolPart.pendingApprovalRequestId : undefined).toBeUndefined();
	});

	it("clears a lingering pending-approval waiting card on a client-driven terminal (markNodeChatStreamTerminated)", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"delete it",
			"2026-05-24T00:00:01.000Z",
		);
		const approval = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({
				type: nodeChatStreamEventTypes.approvalRequested,
				toolCallId: "call-deny",
				toolName: "delete_file",
				approvalRequestId: "approval-99",
				content: null,
				delta: null,
			}),
		);

		const cancelled = markNodeChatStreamTerminated(approval.conversation, "assistant-1", "cancelled");

		const toolPart = cancelled.conversation.messages
			.find((message) => message.id === "assistant-1")
			?.parts?.find((part) => part.kind === "tool");
		expect(toolPart).toMatchObject({ id: "call-deny", state: "failed" });
		expect(toolPart && "pendingApprovalRequestId" in toolPart ? toolPart.pendingApprovalRequestId : undefined).toBeUndefined();
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
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallRequested,
				sequence: 4,
				toolCallId: "call-1",
				toolName: "get_time",
				arguments: "{}",
				content: null,
				delta: null,
			}),
		);
		const toolCompleted = applyNodeChatStreamEvent(
			toolRequested.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallCompleted,
				sequence: 5,
				toolCallId: "call-1",
				toolName: "get_time",
				result: "12:00",
				isError: false,
				content: null,
				delta: null,
			}),
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
		expect(parts[1]).toMatchObject({
			kind: "tool",
			id: "call-1",
			name: "get_time",
			state: "received",
			args: "{}",
			result: "12:00",
		});
		expect(parts[2]).toMatchObject({ kind: "reasoning", text: "the tool says noon" });
	});

	it("appends consecutive reasoning deltas into the same trailing segment", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hi",
			"2026-05-24T00:00:01.000Z",
		);

		const first = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({ sequence: 3, content: null, delta: null, reasoningDelta: "think" }),
		);
		const second = applyNodeChatStreamEvent(
			first.conversation,
			streamEvent({ sequence: 4, content: null, delta: null, reasoningDelta: "ing more" }),
		);

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
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallRequested,
				sequence: 3,
				toolCallId: "call-1",
				toolName: "get_time",
				content: null,
				delta: null,
			}),
		);
		const completed = applyNodeChatStreamEvent(
			requested.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallCompleted,
				sequence: 4,
				toolCallId: "call-1",
				toolName: "get_time",
				result: "12:00",
				isError: false,
				content: null,
				delta: null,
			}),
		);
		// A second completed event for the same id (e.g. a resume replay) must collapse onto the same part.
		const completedAgain = applyNodeChatStreamEvent(
			completed.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallCompleted,
				sequence: 5,
				toolCallId: "call-1",
				toolName: "get_time",
				result: "12:00",
				isError: false,
				content: null,
				delta: null,
			}),
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

	it("appends a notice part without disturbing existing reasoning/tool parts and keeps the turn live", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hi",
			"2026-05-24T00:00:01.000Z",
		);

		const reasoning = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({ sequence: 3, content: null, delta: null, reasoningDelta: "thinking" }),
		);
		const toolRequested = applyNodeChatStreamEvent(
			reasoning.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallRequested,
				sequence: 4,
				toolCallId: "call-1",
				toolName: "get_time",
				content: null,
				delta: null,
			}),
		);
		const noticed = applyNodeChatStreamEvent(
			toolRequested.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.assistantNotice,
				sequence: 5,
				noticeKind: "ModelSubstituted",
				noticeMessage: "Switched to a smaller model to fit available memory.",
				noticeDetail: "qwen3-1.7b",
				content: null,
				delta: null,
			}),
		);

		expect(noticed.isTerminal).toBe(false);
		expect(noticed.timelineEntry).toBeUndefined();
		expect(noticed.streamingMessage).toMatchObject({ messageId: "assistant-1", isActive: true });

		const parts = noticed.streamingMessage.parts ?? [];
		expect(parts.map((part) => part.kind)).toEqual(["reasoning", "tool", "notice"]);
		expect(parts[2]).toMatchObject({
			kind: "notice",
			noticeKind: "ModelSubstituted",
			// The payload's structured detail reaches the part instead of being dropped at the mapper.
			detail: "qwen3-1.7b",
			text: "Switched to a smaller model to fit available memory.",
		});
		// The turn's status/content must stay untouched by the notice event.
		expect(noticed.conversation.messages.find((message) => message.id === "assistant-1")).toMatchObject({
			status: "streaming",
			content: "",
		});
	});

	it("preserves a prior notice part when a later reasoning delta arrives on the same turn", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"hi",
			"2026-05-24T00:00:01.000Z",
		);

		const noticed = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({
				type: nodeChatStreamEventTypes.assistantNotice,
				sequence: 3,
				noticeKind: "HistoryTruncated",
				noticeMessage: "Older messages were trimmed to fit the context window.",
				content: null,
				delta: null,
			}),
		);
		const reasoning = applyNodeChatStreamEvent(
			noticed.conversation,
			streamEvent({ sequence: 4, content: null, delta: null, reasoningDelta: "continuing" }),
		);

		const parts = reasoning.streamingMessage.parts ?? [];
		expect(parts.map((part) => part.kind)).toEqual(["notice", "reasoning"]);
		expect(parts[0]).toMatchObject({ kind: "notice", noticeKind: "HistoryTruncated" });
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

	it("attaches the parsed ask_user question to the matching tool card and keeps the turn live", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"which auth?",
			"2026-05-24T00:00:01.000Z",
		);
		const requested = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallRequested,
				toolCallId: "call-ask",
				toolName: "ask_user",
				content: null,
				delta: null,
			}),
		);

		const question = applyNodeChatStreamEvent(
			requested.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.questionRequested,
				toolCallId: "call-ask",
				toolName: "ask_user",
				questionRequestId: "question-42",
				// The backend serializes `UserQuestionSpec[]` directly, and its non-nullable `Header` rides as "" when
				// the model omitted one — the parser must treat that as absent, not as an empty heading.
				questions: JSON.stringify([
					{
						header: "",
						question: "Which auth method?",
						multiSelect: false,
						options: [
							{ label: "OAuth device flow", description: null, recommended: true },
							{ label: "API key", description: null, recommended: false },
						],
					},
				]),
				content: null,
				delta: null,
			}),
		);

		const toolPart = question.streamingMessage.parts?.find((part) => part.kind === "tool");
		expect(toolPart).toMatchObject({
			kind: "tool",
			id: "call-ask",
			state: "waiting",
			pendingQuestion: {
				requestId: "question-42",
				questions: [
					{
						header: undefined,
						question: "Which auth method?",
						multiSelect: false,
						options: [
							{ label: "OAuth device flow", description: undefined, recommended: true },
							{ label: "API key", description: undefined, recommended: false },
						],
					},
				],
			},
		});
		// Like the approval prompt, a question never mutates content/status and yields no timeline entry.
		expect(question.timelineEntry).toBeUndefined();
		expect(question.isTerminal).toBe(false);
		expect(question.streamingMessage.isActive).toBe(true);
	});

	it("clears the pending question once the answered ask_user call completes", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"which auth?",
			"2026-05-24T00:00:01.000Z",
		);
		const question = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({
				type: nodeChatStreamEventTypes.questionRequested,
				toolCallId: "call-ask",
				toolName: "ask_user",
				questionRequestId: "question-42",
				questions: JSON.stringify([{ question: "Which?", options: [{ label: "a" }, { label: "b" }] }]),
				content: null,
				delta: null,
			}),
		);
		expect(question.streamingMessage.parts?.find((part) => part.kind === "tool")).toMatchObject({ state: "waiting" });

		const completed = applyNodeChatStreamEvent(
			question.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.toolCallCompleted,
				toolCallId: "call-ask",
				toolName: "ask_user",
				result: '{"answers":[{"question":"Which?","selected":["a"]}]}',
				isError: false,
				content: null,
				delta: null,
			}),
		);

		const toolPart = completed.streamingMessage.parts?.find((part) => part.kind === "tool");
		expect(toolPart).toMatchObject({ id: "call-ask", state: "received" });
		expect(toolPart && "pendingQuestion" in toolPart ? toolPart.pendingQuestion : undefined).toBeUndefined();
	});

	it("retires an unanswered question card when the turn terminalizes", () => {
		const optimistic = appendOptimisticNodeChatSend(
			conversation,
			{ userMessageId: "user-1", assistantMessageId: "assistant-1", requestId: "request-1" },
			"which auth?",
			"2026-05-24T00:00:01.000Z",
		);
		const question = applyNodeChatStreamEvent(
			optimistic,
			streamEvent({
				type: nodeChatStreamEventTypes.questionRequested,
				toolCallId: "call-ask",
				toolName: "ask_user",
				questionRequestId: "question-42",
				questions: JSON.stringify([{ question: "Which?", options: [{ label: "a" }, { label: "b" }] }]),
				content: null,
				delta: null,
			}),
		);

		const failed = applyNodeChatStreamEvent(
			question.conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.assistantFailed,
				status: "failed",
				content: null,
				delta: null,
				error: "The turn was interrupted.",
			}),
		);

		const toolPart = failed.streamingMessage.parts?.find((part) => part.kind === "tool");
		expect(toolPart).toMatchObject({ id: "call-ask", state: "failed" });
		expect(toolPart && "pendingQuestion" in toolPart ? toolPart.pendingQuestion : undefined).toBeUndefined();
	});

	it("ignores a malformed question payload rather than rendering a half-built card", () => {
		const question = applyNodeChatStreamEvent(
			conversation,
			streamEvent({
				type: nodeChatStreamEventTypes.questionRequested,
				toolCallId: "call-ask",
				toolName: "ask_user",
				questionRequestId: "question-42",
				questions: "{ not json",
				content: null,
				delta: null,
			}),
		);

		const toolPart = question.streamingMessage.parts?.find((part) => part.kind === "tool");
		// The card still shows as a waiting tool call; it just carries no question to answer.
		expect(toolPart).toMatchObject({ id: "call-ask", state: "waiting" });
		expect(toolPart && "pendingQuestion" in toolPart ? toolPart.pendingQuestion : undefined).toBeUndefined();
	});
});

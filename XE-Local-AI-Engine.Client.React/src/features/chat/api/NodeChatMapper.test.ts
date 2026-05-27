import { describe, expect, it } from "vitest";

import type { NodeChatStreamEventDto } from "@/features/chat/api/NodeChatApi";
import type { NodeChatMessageResponseDto } from "@/features/chat/api/NodeChatApi";
import { mapConversation, mapConversationSummary, mapToolCallEvent } from "@/features/chat/api/NodeChatMapper";

function messageDto(overrides: Partial<NodeChatMessageResponseDto> = {}): NodeChatMessageResponseDto {
	return {
		messageId: "message-1",
		conversationId: "conversation-1",
		sequence: 1,
		role: "Assistant",
		content: "Hi",
		status: "completed",
		createdAtUtc: 1_700_000_001_000,
		updatedAtUtc: 1_700_000_002_000,
		origin: "Local",
		...overrides,
	};
}

function mapSingleMessage(overrides: Partial<NodeChatMessageResponseDto> = {}) {
	const [message] = mapConversation({
		conversationId: "conversation-1",
		title: "t",
		userId: null,
		createdAtUtc: 1_700_000_000_000,
		lastSeenUtc: 1_700_000_002_000,
		purged: false,
		origin: "Local",
		isPinned: false,
		archived: false,
		messages: [messageDto(overrides)],
	}).messages;
	if (!message) {
		throw new Error("expected one mapped message");
	}

	return message;
}

function streamEvent(overrides: Partial<NodeChatStreamEventDto>): NodeChatStreamEventDto {
	return {
		type: "assistant-streaming",
		conversationId: "conversation-1",
		messageId: "message-1",
		requestId: "request-1",
		status: "streaming",
		sequence: 1,
		occurredAtUtc: 1_700_000_000_000,
		...overrides,
	};
}

describe("node chat mapper", () => {
	it("maps conversation summaries into local chat list models", () => {
		expect(
			mapConversationSummary({
				conversationId: "conversation-1",
				title: " Local thread ",
				createdAtUtc: 1_700_000_000_000,
				lastSeenUtc: 1_700_000_001_000,
				lastMessagePreview: "Hello",
				lastMessageStatus: "completed",
				purged: false,
				origin: "Remote",
				isPinned: true,
				archived: false,
			}),
		).toMatchObject({
			id: "conversation-1",
			title: "Local thread",
			createdAt: "2023-11-14T22:13:20.000Z",
			updatedAt: "2023-11-14T22:13:21.000Z",
			lastMessagePreview: "Hello",
			origin: "remote",
			isPinned: true,
			isArchived: false,
			messages: [],
		});
	});

	it("maps detailed conversations and normalizes message role/status values", () => {
		const conversation = mapConversation({
			conversationId: "conversation-1",
			title: null,
			userId: null,
			createdAtUtc: 1_700_000_000_000,
			lastSeenUtc: 1_700_000_002_000,
			purged: false,
			origin: "Local",
			isPinned: false,
			archived: true,
			messages: [
				{
					messageId: "message-1",
					conversationId: "conversation-1",
					requestId: "request-1",
					sequence: 2,
					role: "Assistant",
					content: "Hi",
					reasoning: null,
					status: "Streaming",
					createdAtUtc: 1_700_000_001_000,
					updatedAtUtc: 1_700_000_002_000,
					origin: "Local",
					model: "local-model",
					error: null,
					inputTokens: 10,
					outputTokens: 2,
					totalTokens: 12,
					reasoningTokens: 1,
				},
				{
					messageId: "message-2",
					conversationId: "conversation-1",
					requestId: "request-2",
					sequence: 3,
					role: "Assistant",
					content: "",
					reasoning: null,
					status: "queued",
					createdAtUtc: 1_700_000_003_000,
					updatedAtUtc: 1_700_000_003_000,
					origin: "Local",
					model: null,
					error: null,
					inputTokens: null,
					outputTokens: null,
					totalTokens: null,
					reasoningTokens: null,
				},
			],
		});

		expect(conversation.title).toBe("Untitled conversation");
		// isArchived maps from the dedicated `archived` column (M2), not `purged`.
		expect(conversation.isArchived).toBe(true);
		expect(conversation.isPinned).toBe(false);
		// New keys (feedbackRating/feedbackComment) are undefined here; toEqual ignores undefined props.
		expect(conversation.messages).toEqual([
			{
				id: "message-1",
				conversationId: "conversation-1",
				requestId: "request-1",
				role: "assistant",
				content: "Hi",
				reasoning: undefined,
				status: "streaming",
				createdAt: "2023-11-14T22:13:21.000Z",
				updatedAt: "2023-11-14T22:13:22.000Z",
				sortOrder: 2,
				model: "local-model",
				error: undefined,
				origin: "local",
				inputTokens: 10,
				outputTokens: 2,
				totalTokens: 12,
				reasoningTokens: 1,
				parentMessageId: undefined,
				variantGroupId: undefined,
			},
			{
				id: "message-2",
				conversationId: "conversation-1",
				requestId: "request-2",
				role: "assistant",
				content: "",
				reasoning: undefined,
				status: "queued",
				createdAt: "2023-11-14T22:13:23.000Z",
				updatedAt: "2023-11-14T22:13:23.000Z",
				sortOrder: 3,
				model: undefined,
				error: undefined,
				origin: "local",
				inputTokens: undefined,
				outputTokens: undefined,
				totalTokens: undefined,
				reasoningTokens: undefined,
				parentMessageId: undefined,
				variantGroupId: undefined,
			},
		]);
	});

	it("maps message-borne feedback (rating + comment) onto the message model", () => {
		expect(mapSingleMessage({ feedbackRating: "up", feedbackComment: "Clear answer" })).toMatchObject({
			feedbackRating: "up",
			feedbackComment: "Clear answer",
		});
	});

	it("normalizes the feedback rating casing", () => {
		expect(mapSingleMessage({ feedbackRating: "DOWN" }).feedbackRating).toBe("down");
	});

	it("leaves feedback undefined when no rating is present, even if a stray comment is returned", () => {
		const message = mapSingleMessage({ feedbackRating: null, feedbackComment: "ignored" });

		expect(message.feedbackRating).toBeUndefined();
		expect(message.feedbackComment).toBeUndefined();
	});
});

describe("node chat tool-call event mapper", () => {
	it("maps a tool-call-requested event without approval into a requesting tool call", () => {
		expect(
			mapToolCallEvent(
				streamEvent({
					type: "tool-call-requested",
					toolCallId: "call-1",
					toolName: "search_docs",
					arguments: '{"query":"abc"}',
					requiresApproval: false,
				}),
			),
		).toEqual({
			id: "call-1",
			name: "search_docs",
			state: "requesting",
			args: '{"query":"abc"}',
			requiresApproval: false,
		});
	});

	it("maps a tool-call-requested event requiring approval into a waiting tool call carrying the flag", () => {
		expect(
			mapToolCallEvent(
				streamEvent({
					type: "tool-call-requested",
					toolCallId: "call-2",
					toolName: "delete_file",
					requiresApproval: true,
				}),
			),
		).toEqual({
			id: "call-2",
			name: "delete_file",
			state: "waiting",
			args: undefined,
			requiresApproval: true,
		});
	});

	it("maps a successful tool-call-completed event into a received tool call", () => {
		expect(
			mapToolCallEvent(
				streamEvent({
					type: "tool-call-completed",
					toolCallId: "call-1",
					toolName: "search_docs",
					result: "3 results",
					isError: false,
				}),
			),
		).toEqual({
			id: "call-1",
			name: "search_docs",
			state: "received",
			result: "3 results",
		});
	});

	it("maps a failed tool-call-completed event into a failed tool call", () => {
		expect(
			mapToolCallEvent(
				streamEvent({
					type: "tool-call-completed",
					toolCallId: "call-1",
					toolName: "search_docs",
					result: "boom",
					isError: true,
				}),
			),
		).toMatchObject({ id: "call-1", state: "failed", result: "boom" });
	});

	it("falls back to the message id when no tool call id is present", () => {
		expect(mapToolCallEvent(streamEvent({ type: "tool-call-requested", messageId: "message-9", toolName: "noop" }))?.id).toBe("message-9");
	});

	it("returns null for non-tool stream events", () => {
		expect(mapToolCallEvent(streamEvent({ type: "assistant-delta", delta: "hi" }))).toBeNull();
	});
});

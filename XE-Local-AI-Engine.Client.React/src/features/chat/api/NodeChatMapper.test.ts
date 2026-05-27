import { describe, expect, it } from "vitest";

import { mapConversation, mapConversationSummary } from "@/features/chat/api/NodeChatMapper";

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
});

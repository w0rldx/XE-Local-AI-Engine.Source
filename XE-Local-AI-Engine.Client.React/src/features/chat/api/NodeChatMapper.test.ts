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
			}),
		).toMatchObject({
			id: "conversation-1",
			title: "Local thread",
			createdAt: "2023-11-14T22:13:20.000Z",
			updatedAt: "2023-11-14T22:13:21.000Z",
			lastMessagePreview: "Hello",
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
					model: "local-model",
					error: null,
					inputTokens: 10,
					outputTokens: 2,
					totalTokens: 12,
					reasoningTokens: 1,
				},
			],
		});

		expect(conversation.title).toBe("Untitled conversation");
		expect(conversation.messages).toEqual([
			{
				id: "message-1",
				conversationId: "conversation-1",
				role: "assistant",
				content: "Hi",
				reasoning: undefined,
				status: "streaming",
				createdAt: "2023-11-14T22:13:21.000Z",
				updatedAt: "2023-11-14T22:13:22.000Z",
				sortOrder: 2,
				model: "local-model",
				error: undefined,
				inputTokens: 10,
				outputTokens: 2,
				totalTokens: 12,
				reasoningTokens: 1,
			},
		]);
	});
});

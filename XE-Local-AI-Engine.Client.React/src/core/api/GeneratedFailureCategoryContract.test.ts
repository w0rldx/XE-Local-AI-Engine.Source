import { describe, expect, it } from "vitest";
import { zGetInvocationMonitorResponse, zGetNodeChatConversationResponse } from "@/core/api/generated/zod.gen";

// The failure category is a closed enum on the wire: a client generated before a new value would reject the whole
// conversation or monitor response that carries it, so both responses must decode an ejected turn and an old row.
const conversationId = "0b5a3f92-3a86-4c56-9d3e-2f6d3a1c9a01";

function message(messageId: string, sequence: number, extra: Record<string, unknown>) {
	return {
		messageId,
		conversationId,
		sequence,
		role: "assistant",
		content: "",
		status: "cancelled",
		createdAtUtc: 1_784_000_000_000,
		updatedAtUtc: 1_784_000_001_000,
		origin: "chat",
		...extra,
	};
}

function historyEntry(invocationId: string, extra: Record<string, unknown>) {
	return {
		invocationId,
		conversationId,
		status: "Cancelled",
		startedAt: "2026-11-19T10:00:00+00:00",
		completedAt: "2026-11-19T10:00:05+00:00",
		durationMs: 5000,
		streamedChunkCount: 3,
		streamedThinkingChunkCount: 0,
		...extra,
	};
}

describe("generated failure category contract", () => {
	it("decodes a conversation holding an ejected turn beside an old turn without a category", () => {
		const parsed = zGetNodeChatConversationResponse.parse({
			conversationId,
			createdAtUtc: 1_784_000_000_000,
			lastSeenUtc: 1_784_000_001_000,
			purged: false,
			origin: "chat",
			isPinned: false,
			archived: false,
			memoryExcluded: false,
			isPlain: false,
			messages: [
				message("8f0f2f4e-51f2-4a3e-9d1f-64de6f0b7c22", 1, {
					error: "The model was unloaded.",
					failureCategory: "ModelEjected",
				}),
				message("5d3c1a2b-7e6f-4a8b-9c0d-1e2f3a4b5c6d", 2, {}),
			],
		});

		expect(parsed.messages.map((entry) => entry.failureCategory)).toEqual(["ModelEjected", undefined]);
	});

	it("decodes an invocation monitor whose current run and history carry an ejected run beside an old row", () => {
		const parsed = zGetInvocationMonitorResponse.parse({
			current: {
				invocationId: "3e4f5a6b-7c8d-4e9f-8a0b-1c2d3e4f5a6b",
				conversationId,
				status: "Cancelled",
				startedAt: "2026-11-19T10:00:00+00:00",
				lastUpdatedAt: "2026-11-19T10:00:05+00:00",
				failureCategory: "ModelEjected",
				streamedChunkCount: 3,
				streamedThinkingChunkCount: 0,
				pendingToolCallCount: 0,
				hasPendingApproval: false,
				hasPendingQuestion: false,
			},
			live: [],
			history: [
				historyEntry("6b5a4f3e-2d1c-4b0a-9f8e-7d6c5b4a3f2e", { failureCategory: "ModelEjected" }),
				historyEntry("7c6b5a4f-3e2d-4c1b-8a9f-8e7d6c5b4a3f", {}),
			],
			historyCapacity: 50,
		});

		expect(parsed.current?.failureCategory).toBe("ModelEjected");
		expect(parsed.history.map((entry) => entry.failureCategory)).toEqual(["ModelEjected", undefined]);
	});
});

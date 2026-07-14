import { beforeEach, describe, expect, it, vi } from "vitest";

// Capture the generated SDK branch call so we can assert the request body carries the selected-revision map.
const branchMock = vi.hoisted(() => vi.fn());

vi.mock("@/core/api/generated", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/core/api/generated")>();
	return { ...actual, branchNodeChatConversation: branchMock };
});

// The adapter imports the connection singleton at module load; the branch path never touches it.
vi.mock("@/features/chat/api/NodeChatConnection", () => ({ nodeChatConnection: {} }));

import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";

const branchResponse = {
	data: { sourceConversationId: "conversation-1", branchedConversationId: "conversation-2", copiedMessageCount: 3 },
};

interface BranchCallArg {
	path: { conversationId: string; messageId: string };
	body: { selectedRevisions?: Record<string, string> };
}

function firstBranchCallArg(): BranchCallArg {
	return branchMock.mock.calls[0]?.[0] as BranchCallArg;
}

describe("nodeChatAdapter.branchConversation", () => {
	beforeEach(() => {
		branchMock.mockClear();
	});

	it("sends the selected-revision map in the request body", async () => {
		branchMock.mockResolvedValueOnce(branchResponse);
		const selectedRevisions = { "group-1": "message-a", "group-2": "message-b" };

		const result = await nodeChatAdapter.branchConversation("conversation-1", "message-x", selectedRevisions);

		expect(branchMock).toHaveBeenCalledTimes(1);
		const callArgs = firstBranchCallArg();
		expect(callArgs.path).toEqual({ conversationId: "conversation-1", messageId: "message-x" });
		expect(callArgs.body).toEqual({ selectedRevisions });
		expect(result.branchedConversationId).toBe("conversation-2");
	});

	it("sends an undefined selection when none is provided (server keeps newest-per-group)", async () => {
		branchMock.mockResolvedValueOnce(branchResponse);

		await nodeChatAdapter.branchConversation("conversation-1", "message-x", undefined);

		const callArgs = firstBranchCallArg();
		expect(callArgs.body).toEqual({ selectedRevisions: undefined });
	});
});

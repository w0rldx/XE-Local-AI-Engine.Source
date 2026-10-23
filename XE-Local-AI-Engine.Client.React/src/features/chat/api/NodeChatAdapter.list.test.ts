import { beforeEach, describe, expect, it, vi } from "vitest";

// Capture the generated SDK list call so we can assert the query string the adapter sends.
const listMock = vi.hoisted(() => vi.fn());

vi.mock("@/core/api/generated", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@/core/api/generated")>();
	return { ...actual, listNodeChatConversations: listMock };
});

// The adapter imports the connection singleton at module load; the list path never touches it.
vi.mock("@/features/chat/api/NodeChatConnection", () => ({ nodeChatConnection: {} }));

import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";

interface ListQuery {
	includeArchived: boolean;
	limit?: number;
}

function firstQuery(): ListQuery {
	const [request] = listMock.mock.calls[0] as [{ query: ListQuery }];
	return request.query;
}

describe("nodeChatAdapter.listConversations", () => {
	beforeEach(() => {
		listMock.mockReset();
		listMock.mockResolvedValue({ data: { items: [], maxMessageSizeKb: 64, maxUploadFileSizeMb: 8 } });
	});

	it("sends the page limit as a query parameter", async () => {
		await nodeChatAdapter.listConversations({ includeArchived: false, limit: 100 });

		expect(firstQuery()).toEqual({ includeArchived: false, limit: 100 });
	});

	it("sends no limit when every conversation is requested", async () => {
		const result = await nodeChatAdapter.listConversations({ includeArchived: true });

		expect(firstQuery().limit).toBeUndefined();
		expect(firstQuery().includeArchived).toBe(true);
		expect(result.maxMessageSizeKb).toBe(64);
		expect(result.maxUploadFileSizeMb).toBe(8);
	});
});

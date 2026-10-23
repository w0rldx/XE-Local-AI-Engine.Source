// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { useChatConversationSelection } from "@/features/chat/hooks/useChatConversationSelection";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";

vi.mock("@/features/chat/api/NodeChatAdapter", () => ({
	nodeChatAdapter: { listConversations: vi.fn(), getConversation: vi.fn() },
}));

const listConversations = vi.mocked(nodeChatAdapter.listConversations);

function conversation(id: string): ChatConversationModel {
	return { id, title: id, createdAt: "2026-10-23T00:00:00.000Z", updatedAt: "2026-10-23T00:00:00.000Z", messages: [] };
}

function conversations(count: number): ChatConversationModel[] {
	return Array.from({ length: count }, (_, index) => conversation(`conversation-${index}`));
}

function renderSelection(
	requestedConversationId = "",
	getConversation: (id: string) => Promise<ChatConversationModel> = async (id) => conversation(id),
) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	const wrapper = ({ children }: { children: ReactNode }) => (
		<QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
	);
	// The hook selects the first listed conversation and loads its detail; answer that with a stub payload.
	vi.mocked(nodeChatAdapter.getConversation).mockImplementation(getConversation);
	return renderHook(() => useChatConversationSelection({ requestedConversationId }), { wrapper });
}

describe("useChatConversationSelection conversation page", () => {
	afterEach(() => {
		vi.clearAllMocks();
	});

	it("requests the most recent 100 and flags a full page as truncated", async () => {
		listConversations.mockResolvedValue({ conversations: conversations(100), maxMessageSizeKb: 64 });

		const { result } = renderSelection();

		await waitFor(() => expect(result.current.conversations).toHaveLength(100));
		expect(listConversations).toHaveBeenCalledWith(expect.objectContaining({ includeArchived: false, limit: 100 }));
		expect(result.current.isConversationListTruncated).toBe(true);
		expect(result.current.maxMessageSizeKb).toBe(64);
	});

	it("does not flag a short page", async () => {
		listConversations.mockResolvedValue({ conversations: conversations(99) });

		const { result } = renderSelection();

		await waitFor(() => expect(result.current.conversations).toHaveLength(99));
		expect(result.current.isConversationListTruncated).toBe(false);
	});

	it("keeps a requested conversation that a full page does not list, and fetches it by id", async () => {
		listConversations.mockResolvedValue({ conversations: conversations(100) });

		const { result } = renderSelection("older-thread");

		await waitFor(() => expect(result.current.selectedConversationId).toBe("older-thread"));
		expect(nodeChatAdapter.getConversation).toHaveBeenCalledWith("older-thread", expect.anything());
	});

	it("demotes a requested conversation that a complete list does not contain", async () => {
		listConversations.mockResolvedValue({ conversations: conversations(3) });

		const { result } = renderSelection("older-thread");

		await waitFor(() => expect(result.current.selectedConversationId).toBe("conversation-0"));
	});

	it("keeps the page and the selection on screen while the full list loads", async () => {
		listConversations.mockResolvedValueOnce({ conversations: conversations(100) });
		// The full list never arrives, which pins what the page shows meanwhile.
		listConversations.mockReturnValueOnce(new Promise<never>(() => undefined));

		const { result } = renderSelection();
		await waitFor(() => expect(result.current.selectedConversationId).toBe("conversation-0"));

		act(() => result.current.showAllConversations());

		expect(result.current.selectedConversationId).toBe("conversation-0");
		expect(result.current.displayConversations).toHaveLength(100);
	});

	it("keeps an unlisted requested conversation selected while Show all loads the full list", async () => {
		listConversations.mockResolvedValueOnce({ conversations: conversations(100) });
		// The full list never arrives, which pins what the page shows meanwhile.
		listConversations.mockReturnValueOnce(new Promise<never>(() => undefined));

		const { result } = renderSelection("older-thread");
		await waitFor(() => expect(result.current.selectedConversationId).toBe("older-thread"));

		act(() => result.current.showAllConversations());

		expect(result.current.selectedConversationId).toBe("older-thread");
	});

	it("demotes an unlisted requested conversation once its detail says it is archived and archived ones are hidden", async () => {
		listConversations.mockResolvedValue({ conversations: conversations(100) });

		const { result } = renderSelection("archived-thread", async (id) => ({
			...conversation(id),
			isArchived: id === "archived-thread",
		}));

		await waitFor(() => expect(result.current.selectedConversationId).toBe("conversation-0"));
	});

	it("demotes an unlisted requested conversation after a definitive 404", async () => {
		listConversations.mockResolvedValue({ conversations: conversations(100) });

		const { result } = renderSelection("gone-thread", async (id) => {
			if (id === "gone-thread") {
				throw new ApiError(404, { type: "about:blank", title: "Not Found", status: 404, detail: "gone" });
			}
			return conversation(id);
		});

		await waitFor(() => expect(result.current.selectedConversationId).toBe("conversation-0"));
		expect(result.current.selectedConversationLoadFailed).toBe(false);
	});

	it("re-queries without a limit after Show all and clears the flag", async () => {
		listConversations.mockResolvedValueOnce({ conversations: conversations(100) });
		listConversations.mockResolvedValueOnce({ conversations: conversations(140) });

		const { result } = renderSelection();
		await waitFor(() => expect(result.current.isConversationListTruncated).toBe(true));

		act(() => result.current.showAllConversations());

		await waitFor(() => expect(result.current.conversations).toHaveLength(140));
		expect(listConversations).toHaveBeenLastCalledWith(expect.objectContaining({ includeArchived: false, limit: undefined }));
		expect(result.current.isConversationListTruncated).toBe(false);
		expect(result.current.conversationListQueryKey).toEqual(["node-chat", "conversations", "list", false, false]);
	});
});

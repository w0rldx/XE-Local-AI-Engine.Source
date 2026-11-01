// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";
import { Chat } from "@/features/chat/pages/Chat";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The no-installed-model guidance renders a TanStack-router Link to /models whenever the fixture's model list
// is empty (the default below). Stub the router module so Chat mounts without a RouterProvider (mirrors
// ChatMessage.test.tsx's ModelNotInstalled Link stub).
vi.mock("@tanstack/react-router", async (importOriginal) => {
	const actual = await importOriginal<typeof import("@tanstack/react-router")>();
	return {
		...actual,
		Link: ({ children, to, ...props }: { children: ReactNode; to: string; [key: string]: unknown }) => (
			<a href={to} {...props}>
				{children}
			</a>
		),
	};
});

vi.mock("@/features/chat/api/NodeChatAdapter", () => ({
	nodeChatAdapter: {
		listConversations: vi.fn(),
		getConversation: vi.fn(),
		sendMessage: vi.fn(),
		regenerateMessage: vi.fn(),
		// Chat re-attaches on every conversation open; an idle conversation gets an empty stream back.
		resumeConversation: vi.fn(() => ({
			[Symbol.asyncIterator]: () => ({ next: async () => ({ done: true, value: undefined }) }),
		})),
		deleteConversation: vi.fn(),
		renameConversation: vi.fn(),
		setConversationPinned: vi.fn(),
		setConversationArchived: vi.fn(),
		branchConversation: vi.fn(),
		listMessageRevisions: vi.fn(),
		setMessageFeedback: vi.fn(),
		createConversation: vi.fn(),
		cancelMessage: vi.fn(),
		persistSelectedPath: vi.fn(),
		setConversationPlain: vi.fn(),
		getContextEstimate: vi.fn(),
	},
}));

const { listLocalModelsQueryFn } = vi.hoisted(() => ({
	listLocalModelsQueryFn: vi.fn(),
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	...(await import("@/test/ChatWorkflowQueryStubs")).chatWorkflowQueryStubs,
	listLocalModelsOptions: vi.fn(() => ({
		queryKey: [{ _id: "listLocalModels" }],
		queryFn: () => listLocalModelsQueryFn(),
	})),
	getLocalModelDetailsOptions: vi.fn(() => ({
		queryKey: [{ _id: "getLocalModelDetails" }],
		queryFn: async () => ({}),
	})),
}));

vi.mock("@/features/chat/api/NodeChatConnection", () => ({
	nodeChatConnection: {
		status: "connected",
		subscribe: vi.fn(() => () => undefined),
		ensureConnection: vi.fn(() => Promise.resolve(undefined)),
	},
}));

const adapter = vi.mocked(nodeChatAdapter);

const conversation: ChatConversationModel = {
	id: "conversation-1",
	title: "Thread",
	origin: "local",
	createdAt: "2026-05-24T00:00:00.000Z",
	updatedAt: "2026-05-24T00:00:00.000Z",
	messages: [],
};

// The estimate's key carries neither the plain mode nor the prompt, so changing them must refresh a cached estimate.
describe("Chat plain toggle and the pre-send estimate", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		listLocalModelsQueryFn.mockResolvedValue({
			items: [],
			isAvailable: true,
			selectedModelName: null,
			configuredDefaultModelName: null,
			error: null,
		});
		adapter.listConversations.mockResolvedValue({ conversations: [conversation] });
		adapter.getConversation.mockResolvedValue(conversation);
		adapter.getContextEstimate.mockResolvedValue(null);
		adapter.setConversationPlain.mockResolvedValue({ ...conversation, isPlain: true });
	});

	afterEach(cleanup);

	it("refetches an open estimate after plain mode changes", async () => {
		renderWithProviders(
			<ConfirmContext.Provider value={{ confirm: vi.fn().mockResolvedValue(true) }}>
				<Chat />
			</ConfirmContext.Provider>,
		);
		// The toggle renders once the conversation is selected, so the estimate is for that conversation.
		const toggle = await screen.findByTestId("chat-plain-toggle");
		fireEvent.click(await screen.findByTestId("context-usage-trigger"));
		await waitFor(() => expect(adapter.getContextEstimate).toHaveBeenCalledTimes(1));
		expect(adapter.getContextEstimate.mock.calls[0]?.[0].conversationId).toBe(conversation.id);

		fireEvent.click(toggle);

		await waitFor(() => expect(adapter.setConversationPlain).toHaveBeenCalledTimes(1));
		await waitFor(() => expect(adapter.getContextEstimate).toHaveBeenCalledTimes(2));
	});
});

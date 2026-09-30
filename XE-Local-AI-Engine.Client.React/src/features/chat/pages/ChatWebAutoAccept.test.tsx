// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { nodeChatStreamEventTypes } from "@/features/chat/api/NodeChatStreamState";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";
import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";
import { Chat } from "@/features/chat/pages/Chat";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";
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

function conversationWith(id: string): ChatConversationModel {
	return {
		id,
		title: id,
		origin: "local",
		createdAt: "2026-05-24T00:00:00.000Z",
		updatedAt: "2026-05-24T00:00:00.000Z",
		messages: [
			{
				id: `${id}-assistant`,
				conversationId: id,
				role: "assistant",
				content: "earlier answer",
				status: "completed",
				createdAt: "2026-05-24T00:00:01.000Z",
				sortOrder: 1,
			},
		],
	};
}

const autoConversation = conversationWith("conversation-auto");
const reviewConversation = conversationWith("conversation-review");

function completedStream(conversationId: string) {
	return () => ({
		async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
			yield {
				type: nodeChatStreamEventTypes.assistantCompleted,
				conversationId,
				messageId: "assistant-new",
				requestId: "request-1",
				status: "completed",
				sequence: 1,
				occurredAtUtc: 1_700_000_000_000,
				content: "final answer",
			};
		},
	});
}

function renderChat(selectedConversationId: string) {
	useNodeChatPreferencesStore.setState({ selectedConversationId });
	renderWithProviders(
		<ConfirmContext.Provider value={{ confirm: vi.fn().mockResolvedValue(true) }}>
			<Chat />
		</ConfirmContext.Provider>,
	);
}

// ADR 0017 D8: the review mode is chosen per conversation, and every turn — a send or a regenerate — carries the mode of
// the conversation it runs in. Conversation "auto" was switched to auto-accept; "review" never was.
describe("Chat web content review mode on the wire", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		localStorage.clear();
		localStorage.setItem("xe-node-chat-web-auto:conversation-auto", "true");
		listLocalModelsQueryFn.mockResolvedValue({
			items: [],
			isAvailable: true,
			selectedModelName: null,
			configuredDefaultModelName: null,
			error: null,
		});
		adapter.listConversations.mockResolvedValue({ conversations: [autoConversation, reviewConversation] });
		adapter.getConversation.mockImplementation(async (id) =>
			id === autoConversation.id ? autoConversation : reviewConversation,
		);
	});

	afterEach(() => {
		cleanup();
		localStorage.clear();
		useNodeChatPreferencesStore.setState({ selectedConversationId: "" });
	});

	it.each([
		[autoConversation.id, true],
		[reviewConversation.id, false],
	])("a send in %s carries autoAcceptWebContent=%s", async (conversationId, expected) => {
		adapter.sendMessage.mockImplementation(completedStream(conversationId));
		renderChat(conversationId);

		const input = await screen.findByTestId("chat-input");
		await screen.findByText("earlier answer");
		fireEvent.change(input, { target: { value: "hello" } });
		const send = screen.getByTestId<HTMLButtonElement>("chat-send-button");
		await waitFor(() => expect(send.disabled).toBe(false));
		fireEvent.click(screen.getByTestId("chat-send-button"));

		await waitFor(() => expect(adapter.sendMessage).toHaveBeenCalledTimes(1));
		expect(adapter.sendMessage.mock.calls[0]?.[0].conversationId).toBe(conversationId);
		expect(adapter.sendMessage.mock.calls[0]?.[0].autoAcceptWebContent).toBe(expected);
	});

	it.each([
		[autoConversation.id, true],
		[reviewConversation.id, false],
	])("a regenerate in %s carries autoAcceptWebContent=%s", async (conversationId, expected) => {
		adapter.regenerateMessage.mockImplementation(completedStream(conversationId));
		renderChat(conversationId);

		await screen.findByText("earlier answer");
		fireEvent.click(await screen.findByLabelText("Regenerate response"));

		await waitFor(() => expect(adapter.regenerateMessage).toHaveBeenCalledTimes(1));
		const call = adapter.regenerateMessage.mock.calls[0];
		expect(call?.[0]).toBe(conversationId);
		// (conversationId, messageId, effort, tools, knowledgeBase, selectedPath, sampling, autoAcceptWebContent, signal)
		expect(call?.[7]).toBe(expected);
	});

	it("removes the conversation's stored review mode once the conversation is deleted", async () => {
		renderChat(reviewConversation.id);

		fireEvent.click(await screen.findByTestId(`conversation-actions-${autoConversation.id}`));
		fireEvent.click(await screen.findByTestId(`conversation-delete-${autoConversation.id}`), { shiftKey: true });

		await waitFor(() => expect(adapter.deleteConversation).toHaveBeenCalledWith(autoConversation.id));
		await waitFor(() => expect(localStorage.getItem("xe-node-chat-web-auto:conversation-auto")).toBeNull());
	});
});

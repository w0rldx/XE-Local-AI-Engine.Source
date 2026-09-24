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
		// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
		queryKey: [{ _id: "listLocalModels" }],
		queryFn: () => listLocalModelsQueryFn(),
	})),
	getLocalModelDetailsOptions: vi.fn(() => ({
		// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
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

// One indexed document in DEFAULT, so the composer's knowledge-base toggle is enabled.
vi.mock("@/features/knowledge/queries/useKnowledgeDocuments", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/features/knowledge/queries/useKnowledgeDocuments")>()),
	useKnowledgeDocuments: vi.fn(() => ({ data: [{ status: "Indexed", chunkCount: 1 }] })),
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

const completed: NodeChatStreamEventDto = {
	type: nodeChatStreamEventTypes.assistantCompleted,
	conversationId: conversation.id,
	messageId: "assistant-1",
	requestId: "request-1",
	status: "completed",
	sequence: 1,
	occurredAtUtc: 1_700_000_000_000,
	content: "final answer",
};

async function sendAndReadFlag(): Promise<boolean | undefined> {
	const input = await screen.findByTestId("chat-input");
	fireEvent.change(input, { target: { value: "hello" } });
	fireEvent.click(screen.getByTestId("chat-send-button"));
	await waitFor(() => expect(adapter.sendMessage).toHaveBeenCalledTimes(1));
	return adapter.sendMessage.mock.calls[0]?.[0].useKnowledgeBase;
}

// F-17: the flag the SendMessage frame carries must be the state the toggle shows, never its inverse.
describe("Chat knowledge-base toggle and the send payload", () => {
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
		adapter.sendMessage.mockImplementation(() => ({
			async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
				yield completed;
			},
		}));
	});

	afterEach(() => {
		cleanup();
		useNodeChatPreferencesStore.setState({ knowledgeBaseEnabled: false });
	});

	it.each([true, false])("sends useKnowledgeBase=%s when the toggle shows that state", async (enabled) => {
		useNodeChatPreferencesStore.setState({ knowledgeBaseEnabled: enabled });
		renderWithProviders(
			<ConfirmContext.Provider value={{ confirm: vi.fn().mockResolvedValue(true) }}>
				<Chat />
			</ConfirmContext.Provider>,
		);
		await screen.findByTestId("conversation-item-conversation-1");
		const toggle = await screen.findByTestId<HTMLButtonElement>("chat-knowledge-base-toggle");
		await waitFor(() => expect(toggle.getAttribute("aria-pressed")).toBe(String(enabled)));

		expect(await sendAndReadFlag()).toBe(enabled);
	});

	it.each([true, false])("sends the toggled state after a click from %s", async (initial) => {
		useNodeChatPreferencesStore.setState({ knowledgeBaseEnabled: initial });
		renderWithProviders(
			<ConfirmContext.Provider value={{ confirm: vi.fn().mockResolvedValue(true) }}>
				<Chat />
			</ConfirmContext.Provider>,
		);
		await screen.findByTestId("conversation-item-conversation-1");
		const toggle = await screen.findByTestId<HTMLButtonElement>("chat-knowledge-base-toggle");
		await waitFor(() => expect(toggle.disabled).toBe(false));
		fireEvent.click(toggle);
		await waitFor(() => expect(toggle.getAttribute("aria-pressed")).toBe(String(!initial)));

		expect(await sendAndReadFlag()).toBe(!initial);
	});
});

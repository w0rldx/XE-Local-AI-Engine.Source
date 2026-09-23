// @vitest-environment jsdom

import type { QueryClient } from "@tanstack/react-query";
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { nodeChatStreamEventTypes } from "@/features/chat/api/NodeChatStreamState";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";
import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";
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

const adapter = vi.mocked(nodeChatAdapter);

function makeConversation(id: string, title: string): ChatConversationModel {
	return {
		id,
		title,
		origin: "local",
		createdAt: "2026-05-24T00:00:00.000Z",
		updatedAt: "2026-05-24T00:00:00.000Z",
		messages: [],
	};
}

function deltaEvent(conversationId: string): NodeChatStreamEventDto {
	return {
		type: nodeChatStreamEventTypes.assistantStreaming,
		conversationId,
		messageId: "assistant-1",
		requestId: "request-1",
		status: "streaming",
		sequence: 1,
		occurredAtUtc: 1_700_000_000_000,
		delta: "partial",
		content: "partial",
	};
}

function completedEvent(conversationId: string): NodeChatStreamEventDto {
	return {
		type: nodeChatStreamEventTypes.assistantCompleted,
		conversationId,
		messageId: "assistant-1",
		requestId: "request-1",
		status: "completed",
		sequence: 2,
		occurredAtUtc: 1_700_000_000_001,
		content: "final answer",
	};
}

function renderChat(): { queryClient: QueryClient } {
	const confirmValue = { confirm: vi.fn().mockResolvedValue(true) };

	return renderWithProviders(
		<ConfirmContext.Provider value={confirmValue}>
			<Chat />
		</ConfirmContext.Provider>,
	);
}

describe("Chat send-conversation resolution", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		listLocalModelsQueryFn.mockResolvedValue({
			items: [],
			isAvailable: true,
			selectedModelName: null,
			configuredDefaultModelName: null,
			error: null,
		});
	});

	afterEach(() => {
		cleanup();
	});

	it("sends to the newly-selected conversation, never the placeholder one, when Enter fires before its fetch resolves", async () => {
		const conversationA = makeConversation("conversation-a", "Thread A");
		const conversationB = makeConversation("conversation-b", "Thread B");
		adapter.listConversations.mockResolvedValue({ conversations: [conversationA, conversationB] });

		// Conversation B's full payload is deferred so that selecting B keeps A on screen via keepPreviousData
		// (isPlaceholderData), reproducing the fast-switch-then-Enter window.
		let resolveB: ((value: ChatConversationModel) => void) | undefined;
		adapter.getConversation.mockImplementation(async (id: string) => {
			if (id === "conversation-a") {
				return conversationA;
			}
			return new Promise<ChatConversationModel>((resolve) => {
				resolveB = resolve;
			});
		});

		adapter.sendMessage.mockImplementation(() => ({
			async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
				yield deltaEvent("conversation-b");
				yield completedEvent("conversation-b");
			},
		}));

		renderChat();
		// A loads first (default selection = first conversation).
		await screen.findByTestId("conversation-item-conversation-a");
		const input = await screen.findByTestId("chat-input");

		// Switch to B — its fetch is deferred, so A's payload stays mounted as placeholder data.
		fireEvent.click(screen.getByTestId("conversation-item-conversation-b"));

		// Fire Enter immediately, before B's payload settles.
		fireEvent.change(input, { target: { value: "hello B" } });
		fireEvent.keyDown(input, { key: "Enter" });

		// Let B's deferred fetch settle so the load-by-id path can resolve the correct conversation.
		await waitFor(() => expect(resolveB).toBeDefined());
		resolveB?.(conversationB);

		await waitFor(() => expect(adapter.sendMessage).toHaveBeenCalledTimes(1));
		// The send must target B — the previous conversation's id must never be used.
		for (const [request] of adapter.sendMessage.mock.calls) {
			expect((request as { conversationId: string }).conversationId).toBe("conversation-b");
		}
	});

	it("invalidates the local-model-details query after a completed turn so the context meter re-reads the effective window", async () => {
		const conversation = makeConversation("conversation-1", "Thread");
		adapter.listConversations.mockResolvedValue({ conversations: [conversation] });
		adapter.getConversation.mockResolvedValue(conversation);
		adapter.sendMessage.mockImplementation(() => ({
			async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
				yield deltaEvent("conversation-1");
				yield completedEvent("conversation-1");
			},
		}));

		const { queryClient } = renderChat();
		const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries");

		await screen.findByTestId("conversation-item-conversation-1");
		const input = await screen.findByTestId("chat-input");

		fireEvent.change(input, { target: { value: "hello" } });
		fireEvent.click(screen.getByTestId("chat-send-button"));

		await waitFor(() => expect(adapter.sendMessage).toHaveBeenCalledTimes(1));

		// The terminal refresh must invalidate the model-details query (partial-object match on the single-element
		// hey-api key), so a pre-warm capacity of 262k gives way to the real launched window once the model is warm.
		await waitFor(() => {
			const invalidatedDetails = invalidateSpy.mock.calls.some((call) => {
				const key = (call[0] as { queryKey?: readonly unknown[] } | undefined)?.queryKey;
				const first = key?.[0] as { _id?: string } | undefined;
				return first?._id === "getLocalModelDetails";
			});
			expect(invalidatedDetails).toBe(true);
		});
	});
});

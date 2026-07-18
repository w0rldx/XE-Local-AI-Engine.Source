// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { nodeChatStreamEventTypes } from "@/features/chat/api/NodeChatStreamState";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";
import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";
import { Chat } from "@/features/chat/pages/Chat";

// UX-09's no-installed-model guidance renders a TanStack-router Link to /models whenever the fixture's model list
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

function installJsdomEnvironmentMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();

			unobserve = vi.fn();

			disconnect = vi.fn();
		},
	});
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
	Element.prototype.scrollIntoView = vi.fn();
	if (!("randomUUID" in crypto)) {
		Object.defineProperty(crypto, "randomUUID", { writable: true, value: () => "00000000-0000-4000-8000-000000000000" });
	}
}

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
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false, gcTime: 0 } },
	});
	const confirmValue = { confirm: vi.fn().mockResolvedValue(true) };

	render(
		<QueryClientProvider client={queryClient}>
			<ConfirmContext.Provider value={confirmValue}>
				<MantineProvider>
					<Chat />
				</MantineProvider>
			</ConfirmContext.Provider>
		</QueryClientProvider>,
	);

	return { queryClient };
}

describe("Chat send-conversation resolution (GPTAUD-16 / GPTAUD-17a)", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
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
		adapter.listConversations.mockResolvedValue([conversationA, conversationB]);

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

	it("invalidates the local-model-details query after a completed turn so the context meter re-reads the effective window (GPTAUD-17a)", async () => {
		const conversation = makeConversation("conversation-1", "Thread");
		adapter.listConversations.mockResolvedValue([conversation]);
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

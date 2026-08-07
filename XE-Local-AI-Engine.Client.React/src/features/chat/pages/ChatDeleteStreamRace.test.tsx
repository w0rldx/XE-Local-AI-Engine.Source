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
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";

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

// Chat.tsx repointed its model list/details onto the generated SDK (listLocalModelsOptions /
// getLocalModelDetailsOptions). Partially mock the generated TanStack module so the page's
// useQuery(withResponseValidation(...)) resolves a deterministic empty list without a real request; the real
// withResponseValidation bridge still wraps the mocked queryFn. The list queryFn is hoisted so beforeEach can set
// the resolved payload, mirroring the prior listLocalModels mock.
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

// The A9 readiness gate eager-connects the shared hub on mount; stub it as already connected so the page
// renders past the connecting gate (a real SignalR connection can't be built in jsdom).
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
	// Mantine's autosize Textarea reads document.fonts; the message list scrolls into view on mount. jsdom
	// implements neither, so shim them to keep the full Chat page render stable under test.
	Object.defineProperty(document, "fonts", {
		writable: true,
		value: { ready: Promise.resolve(), addEventListener: vi.fn(), removeEventListener: vi.fn() },
	});
	Element.prototype.scrollIntoView = vi.fn();
	if (!("randomUUID" in crypto)) {
		Object.defineProperty(crypto, "randomUUID", { writable: true, value: () => "00000000-0000-4000-8000-000000000000" });
	}
}

function conversation(): ChatConversationModel {
	return {
		id: "conversation-1",
		title: "Streaming thread",
		origin: "local",
		createdAt: "2026-05-24T00:00:00.000Z",
		updatedAt: "2026-05-24T00:00:00.000Z",
		messages: [],
	};
}

function deltaEvent(): NodeChatStreamEventDto {
	return {
		type: nodeChatStreamEventTypes.assistantStreaming,
		conversationId: "conversation-1",
		messageId: "assistant-1",
		requestId: "request-1",
		status: "streaming",
		sequence: 1,
		occurredAtUtc: 1_700_000_000_000,
		delta: "partial",
		content: "partial",
	};
}

function completedEvent(): NodeChatStreamEventDto {
	return {
		type: nodeChatStreamEventTypes.assistantCompleted,
		conversationId: "conversation-1",
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

describe("Chat delete-vs-stream race", () => {
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
		adapter.getConversation.mockResolvedValue(conversation());
		// The list starts with the one conversation; the delete drops it so a post-delete list refetch (and the
		// selection fallback) cannot legitimately re-fetch the removed thread — isolating the stream race.
		adapter.listConversations.mockResolvedValue({ conversations: [conversation()] });
		adapter.deleteConversation.mockImplementation(async (): Promise<void> => {
			adapter.listConversations.mockResolvedValue({ conversations: [] });
		});
	});

	afterEach(() => {
		cleanup();
	});

	it("aborts the in-flight stream and stops re-caching/refetching when the streaming conversation is deleted", async () => {
		let capturedSignal: AbortSignal | undefined;
		let releaseStream: (() => void) | undefined;
		// The stream yields one event, then blocks until the abort fires (or the gate is released) so the turn
		// stays in flight while the operator deletes it.
		const streamGate = new Promise<void>((resolve) => {
			releaseStream = resolve;
		});
		adapter.sendMessage.mockImplementation((_request, signal) => {
			capturedSignal = signal;
			signal.addEventListener("abort", () => releaseStream?.(), { once: true });
			return {
				async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
					yield deltaEvent();
					await streamGate;
				},
			};
		});

		const { queryClient } = renderChat();

		// Wait until the conversation list and input have hydrated from the mocked adapter.
		await screen.findByTestId("conversation-item-conversation-1");
		const input = await screen.findByTestId("chat-input");

		fireEvent.change(input, { target: { value: "hello" } });
		fireEvent.click(screen.getByTestId("chat-send-button"));

		// The stream is open and has delivered at least one event into the conversation cache.
		await waitFor(() => expect(adapter.sendMessage).toHaveBeenCalledTimes(1));
		await waitFor(() => expect(queryClient.getQueryData(nodeChatQueryKeys.conversation("conversation-1"))).toBeDefined());
		expect(capturedSignal?.aborted).toBe(false);

		// Reset the refetch tracker so we only count getConversation calls that happen AFTER the delete.
		adapter.getConversation.mockClear();

		// Shift-click delete skips the confirm dialog and deletes the actively streaming conversation.
		fireEvent.click(screen.getByTestId("conversation-actions-conversation-1"));
		fireEvent.click(await screen.findByTestId("conversation-delete-conversation-1"), { shiftKey: true });

		// The delete aborts the in-flight stream for that conversation and removes it server-side.
		await waitFor(() => expect(capturedSignal?.aborted).toBe(true));
		await waitFor(() => expect(adapter.deleteConversation).toHaveBeenCalledWith("conversation-1"));

		// Let the aborted iterator unwind so the streaming loop's finally block runs.
		releaseStream?.();
		await waitFor(() => expect(adapter.deleteConversation).toHaveBeenCalledTimes(1));

		// The guarded loop/finally must neither re-create the removed cache entry nor refetch (resurrect) it.
		await waitFor(() => expect(queryClient.getQueryData(nodeChatQueryKeys.conversation("conversation-1"))).toBeUndefined());
		expect(adapter.getConversation.mock.calls.filter((call) => call[0] === "conversation-1")).toHaveLength(0);
	});

	it("rolls back the deleted-conversation marker when the delete fails, so a later send still streams", async () => {
		// The delete request fails, so the conversation survives on the server and stays visible/selectable.
		adapter.deleteConversation.mockReset();
		adapter.deleteConversation.mockRejectedValue(new Error("delete failed"));
		adapter.listConversations.mockResolvedValue({ conversations: [conversation()] });

		// The send streams a delta then a terminal event. `secondEventPulled` flips only if the streaming loop
		// consumes PAST the first event — which it can only do when the deleted marker was rolled back (otherwise
		// the loop's has(id) guard cancels+breaks on the first iteration, and the terminal event is never pulled).
		let secondEventPulled = false;
		adapter.sendMessage.mockImplementation(() => ({
			async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
				yield deltaEvent();
				secondEventPulled = true;
				yield completedEvent();
			},
		}));

		renderChat();
		await screen.findByTestId("conversation-item-conversation-1");
		const input = await screen.findByTestId("chat-input");

		// Shift-click delete skips the confirm; the request rejects and the catch must roll back the marker.
		fireEvent.click(screen.getByTestId("conversation-actions-conversation-1"));
		fireEvent.click(await screen.findByTestId("conversation-delete-conversation-1"), { shiftKey: true });
		await waitFor(() => expect(adapter.deleteConversation).toHaveBeenCalledWith("conversation-1"));

		// Now send into the surviving conversation. The stream must run to completion.
		fireEvent.change(input, { target: { value: "still there?" } });
		fireEvent.click(screen.getByTestId("chat-send-button"));

		await waitFor(() => expect(adapter.sendMessage).toHaveBeenCalledTimes(1));
		await waitFor(() => expect(secondEventPulled).toBe(true));
	});
});

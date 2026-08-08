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

describe("Chat cancel ordering", () => {
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
		adapter.listConversations.mockResolvedValue({ conversations: [conversation()] });
	});

	afterEach(() => {
		cleanup();
	});

	it("aborts the local stream before issuing the server cancel request", async () => {
		let capturedSignal: AbortSignal | undefined;
		let releaseStream: (() => void) | undefined;
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

		// Record whether the local stream was already aborted at the moment the server cancel REST call fired.
		let abortedWhenCancelCalled: boolean | undefined;
		adapter.cancelMessage.mockImplementation(async () => {
			abortedWhenCancelCalled = capturedSignal?.aborted ?? false;
		});

		const { queryClient } = renderChat();

		await screen.findByTestId("conversation-item-conversation-1");
		const input = await screen.findByTestId("chat-input");
		fireEvent.change(input, { target: { value: "hello" } });
		fireEvent.click(screen.getByTestId("chat-send-button"));

		// The stream is open and has delivered its first event, so the send-button is now the in-flight Stop button.
		await waitFor(() => expect(adapter.sendMessage).toHaveBeenCalledTimes(1));
		await waitFor(() => expect(queryClient.getQueryData(nodeChatQueryKeys.conversation("conversation-1"))).toBeDefined());
		expect(capturedSignal?.aborted).toBe(false);

		// Click Stop → handleCancel: it must abort the local stream FIRST, then fire the best-effort server cancel.
		fireEvent.click(screen.getByTestId("chat-send-button"));

		await waitFor(() => expect(adapter.cancelMessage).toHaveBeenCalledTimes(1));
		expect(abortedWhenCancelCalled).toBe(true);
		expect(capturedSignal?.aborted).toBe(true);

		// Let the aborted iterator unwind so the streaming loop's finally block runs.
		releaseStream?.();
		await waitFor(() => expect(adapter.getConversation).toHaveBeenCalled());
	});
});

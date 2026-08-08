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
// is empty (the default below). Stub the router module so Chat mounts without a RouterProvider.
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
		resumeConversation: vi.fn(),
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

// The readiness gate eager-connects the shared hub on mount; stub it as already connected so the page renders
// past the connecting gate (a real SignalR connection can't be built in jsdom).
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

/**
 * The conversation exactly as a RELOADED page loads it: the user turn plus the still-`streaming` assistant
 * placeholder. The pending `ask_user` prompt is deliberately NOT in `parts` — it is live-only state, which is the
 * whole reason the re-attach exists.
 */
function parkedConversation(id = "conversation-1"): ChatConversationModel {
	return {
		id,
		title: "Parked thread",
		origin: "local",
		createdAt: "2026-08-04T00:00:00.000Z",
		updatedAt: "2026-08-04T00:00:00.000Z",
		messages: [
			{
				id: `${id}-user-1`,
				conversationId: id,
				role: "user",
				content: "plan the migration",
				status: "completed",
				createdAt: "2026-08-04T00:00:00.000Z",
				sortOrder: 1,
			},
			{
				id: `${id}-assistant-1`,
				conversationId: id,
				role: "assistant",
				content: "",
				status: "streaming",
				createdAt: "2026-08-04T00:00:01.000Z",
				sortOrder: 2,
			},
		],
	};
}

/**
 * The replayed `question-requested` frame the resume registry emits for a parked turn. A resume stamps the
 * INVOCATION id as the message id, so this also pins the remap onto the persisted assistant row.
 */
function questionRequestedEvent(): NodeChatStreamEventDto {
	return {
		type: nodeChatStreamEventTypes.questionRequested,
		conversationId: "conversation-1",
		messageId: "invocation-1",
		requestId: "invocation-1",
		status: "streaming",
		sequence: 0,
		occurredAtUtc: 1_700_000_000_000,
		toolCallId: "call-1",
		toolName: "ask_user",
		questionRequestId: "question-1",
		questions: JSON.stringify([
			{
				header: "Scope",
				question: "Which services should migrate first?",
				options: [{ label: "Billing" }, { label: "Search", recommended: true }],
			},
		]),
	};
}

/** The hub's answer when nothing is live: a stream that completes immediately, with no events. */
function emptyResumeStream(): AsyncIterable<NodeChatStreamEventDto> {
	return {
		[Symbol.asyncIterator]: () => ({
			next: (): Promise<IteratorResult<NodeChatStreamEventDto>> => Promise.resolve({ done: true, value: undefined }),
		}),
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

describe("Chat cold-load resume", () => {
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
		adapter.listConversations.mockResolvedValue({ conversations: [parkedConversation()] });
		adapter.getConversation.mockImplementation(async (conversationId: string) => parkedConversation(conversationId));
		adapter.cancelMessage.mockResolvedValue(undefined);
	});

	afterEach(() => {
		cleanup();
	});

	it("re-attaches on open and restores the pending question card the reload lost", async () => {
		// The parked run: the replayed question, then nothing — the turn stays in flight until it is answered.
		adapter.resumeConversation.mockImplementation((_conversationId, signal) => ({
			async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
				yield questionRequestedEvent();
				await new Promise<void>((resolve) => signal.addEventListener("abort", () => resolve(), { once: true }));
			},
		}));

		renderChat();
		await screen.findByTestId("conversation-item-conversation-1");

		await waitFor(() => expect(adapter.resumeConversation).toHaveBeenCalledWith("conversation-1", expect.any(AbortSignal)));
		// The card the reload lost is back on screen.
		await screen.findByTestId("chat-ask-user-card");
		expect(screen.getByTestId("chat-ask-user-question-0").textContent).toContain("Which services should migrate first?");

		// The turn genuinely IS still running, so the composer must show the in-flight Stop button — clicking it
		// cancels the RESUMED run: the persisted assistant row (remapped from the invocation-stamped events) and the
		// invocation id the resume rode in on.
		fireEvent.click(screen.getByTestId("chat-send-button"));
		await waitFor(() => expect(adapter.cancelMessage).toHaveBeenCalledTimes(1));
		expect(adapter.cancelMessage).toHaveBeenCalledWith({
			conversationId: "conversation-1",
			messageId: "conversation-1-assistant-1",
			requestId: "invocation-1",
		});
	});

	it("is a silent no-op for an idle conversation", async () => {
		adapter.resumeConversation.mockImplementation(() => emptyResumeStream());

		const { queryClient } = renderChat();
		await screen.findByTestId("conversation-item-conversation-1");
		await waitFor(() => expect(adapter.resumeConversation).toHaveBeenCalledTimes(1));

		const loadedConversation = queryClient.getQueryData(nodeChatQueryKeys.conversation("conversation-1"));
		const loadCalls = adapter.getConversation.mock.calls.length;

		// Nothing to attach to: no error banner, no cache write (same object identity), and no post-turn refetch.
		await waitFor(() => expect(queryClient.getQueryData(nodeChatQueryKeys.conversation("conversation-1"))).toBe(loadedConversation));
		expect(screen.queryByTestId("chat-ask-user-card")).toBeNull();
		expect(screen.queryByText("Local chat stream failed.")).toBeNull();
		expect(adapter.getConversation.mock.calls).toHaveLength(loadCalls);
	});

	it("does not re-attach while a send already owns the turn", async () => {
		adapter.listConversations.mockResolvedValue({ conversations: [parkedConversation(), parkedConversation("conversation-2")] });
		adapter.resumeConversation.mockImplementation(() => emptyResumeStream());
		// The send stays open, so its stream owns the turn for the rest of the test.
		adapter.sendMessage.mockImplementation((_request, signal) => ({
			async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
				yield {
					type: nodeChatStreamEventTypes.assistantStreaming,
					conversationId: "conversation-1",
					messageId: "assistant-live",
					requestId: "request-live",
					status: "streaming",
					sequence: 1,
					occurredAtUtc: 1_700_000_000_000,
					delta: "working",
					content: "working",
				};
				await new Promise<void>((resolve) => signal.addEventListener("abort", () => resolve(), { once: true }));
			},
		}));

		renderChat();
		await screen.findByTestId("conversation-item-conversation-1");
		// One re-attach for the conversation that was open on load; it found nothing live.
		await waitFor(() => expect(adapter.resumeConversation).toHaveBeenCalledTimes(1));

		const input = await screen.findByTestId("chat-input");
		fireEvent.change(input, { target: { value: "hello" } });
		fireEvent.click(screen.getByTestId("chat-send-button"));
		await waitFor(() => expect(adapter.sendMessage).toHaveBeenCalledTimes(1));

		// Leaving and re-opening conversations while that send streams must never open a second subscription.
		fireEvent.click(screen.getByTestId("conversation-item-conversation-2"));
		await waitFor(() => expect(adapter.getConversation).toHaveBeenCalledWith("conversation-2", expect.anything()));
		fireEvent.click(screen.getByTestId("conversation-item-conversation-1"));

		await waitFor(() => expect(screen.getByTestId("conversation-item-conversation-1")).toBeDefined());
		expect(adapter.resumeConversation).toHaveBeenCalledTimes(1);
	});
});

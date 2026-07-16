// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";
import { Chat } from "@/features/chat/pages/Chat";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";

// AUD4-13: a permanently-failing getConversation must surface an inline error + Retry — never an infinite spinner.
// This exercises the Chat-page wiring (query error state → messagesLoadFailed → ChatMessageList error surface), the
// spinner-deadlock fix, retry-refetch recovery, and recovery when switching to a healthy conversation.

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

function summary(id: string, title: string): ChatConversationModel {
	return {
		id,
		title,
		origin: "local",
		createdAt: "2026-05-24T00:00:00.000Z",
		updatedAt: "2026-05-24T00:00:00.000Z",
		messages: [],
	};
}

function loaded(id: string, content: string): ChatConversationModel {
	return {
		...summary(id, "Loaded thread"),
		messages: [
			{
				id: `${id}-user`,
				conversationId: id,
				role: "user",
				content,
				status: "completed",
				createdAt: "2026-05-24T00:00:01.000Z",
				sortOrder: 1,
			},
		],
	};
}

function renderChat(): void {
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
}

describe("Chat selected-conversation load failure (AUD4-13)", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		vi.clearAllMocks();
		// Start from a clean selection so selectedConversationId falls back to the first conversation in the list.
		useNodeChatPreferencesStore.getState().actions.setSelectedConversationId("");
		listLocalModelsQueryFn.mockResolvedValue({
			items: [],
			isAvailable: true,
			selectedModelName: null,
			configuredDefaultModelName: null,
			error: null,
		});
		adapter.listConversations.mockResolvedValue([summary("conversation-1", "First"), summary("conversation-2", "Second")]);
	});

	afterEach(() => {
		cleanup();
		useNodeChatPreferencesStore.getState().actions.setSelectedConversationId("");
	});

	it("renders an inline error with Retry — never an infinite spinner — when the load fails permanently", async () => {
		adapter.getConversation.mockRejectedValue(new Error("Request failed with status code 500"));

		renderChat();

		await screen.findByTestId("chat-messages-load-error");
		expect(screen.getByTestId("chat-messages-load-retry")).toBeTruthy();
		// The spinner-deadlock is gone: the loader is not showing under a permanent failure.
		expect(screen.queryByText("Loading messages…")).toBeNull();
	});

	it("refetches on Retry and renders the messages once the load succeeds", async () => {
		adapter.getConversation
			.mockRejectedValueOnce(new Error("Request failed with status code 500"))
			.mockResolvedValue(loaded("conversation-1", "Recovered message"));

		renderChat();

		await screen.findByTestId("chat-messages-load-error");
		fireEvent.click(screen.getByTestId("chat-messages-load-retry"));

		await waitFor(() => expect(screen.getByText("Recovered message")).toBeTruthy());
		expect(screen.queryByTestId("chat-messages-load-error")).toBeNull();
	});

	it("recovers when the user switches to a healthy conversation while one is failed", async () => {
		adapter.getConversation.mockImplementation(async (id: string) => {
			if (id === "conversation-1") {
				throw new Error("Request failed with status code 500");
			}
			return loaded("conversation-2", "Second thread body");
		});

		renderChat();

		await screen.findByTestId("chat-messages-load-error");
		// Switching to the healthy conversation re-keys the query; the stale error must not persist.
		fireEvent.click(screen.getByTestId("conversation-item-conversation-2"));

		await waitFor(() => expect(screen.getByText("Second thread body")).toBeTruthy());
		expect(screen.queryByTestId("chat-messages-load-error")).toBeNull();
	});

	it("still shows the loader while the load is genuinely in flight", async () => {
		// A never-settling getConversation keeps the query pending — the loader (not the error) must show.
		adapter.getConversation.mockReturnValue(new Promise<ChatConversationModel>(() => undefined));

		renderChat();

		await screen.findByText("Loading messages…");
		expect(screen.queryByTestId("chat-messages-load-error")).toBeNull();
	});
});

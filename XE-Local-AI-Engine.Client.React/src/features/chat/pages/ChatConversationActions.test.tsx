// @vitest-environment jsdom

import type { QueryClient } from "@tanstack/react-query";
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import { nodeChatQueryKeys } from "@/core/chat/queries/NodeChatQueryKeys";
import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import type { ChatConversationModel } from "@/features/chat/models/ChatModels";
import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";
import { Chat } from "@/features/chat/pages/Chat";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The no-installed-model guidance renders a TanStack-router Link to /models. Stub the router module so Chat mounts
// without a RouterProvider (mirrors ChatMessage.test.tsx's ModelNotInstalled Link stub).
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

const viewOnlySentence = "This conversation was started from a paired client and is view-only on this node.";

function conversation(overrides: Partial<ChatConversationModel> = {}): ChatConversationModel {
	return {
		id: "conversation-1",
		title: "Local thread",
		origin: "local",
		createdAt: "2026-05-24T00:00:00.000Z",
		updatedAt: "2026-05-24T00:00:00.000Z",
		messages: [],
		...overrides,
	};
}

function answeredConversation(): ChatConversationModel {
	return conversation({
		messages: [
			{
				id: "user-1",
				conversationId: "conversation-1",
				role: "user",
				content: "question",
				status: "completed",
				createdAt: "2026-05-24T00:00:00.000Z",
				sortOrder: 0,
			},
			{
				id: "assistant-1",
				conversationId: "conversation-1",
				requestId: "request-1",
				role: "assistant",
				content: "answer",
				status: "completed",
				createdAt: "2026-05-24T00:00:01.000Z",
				sortOrder: 1,
			},
		],
	});
}

function renderChat(): { queryClient: QueryClient } {
	const confirmValue = { confirm: vi.fn().mockResolvedValue(true) };

	return renderWithProviders(
		<ConfirmContext.Provider value={confirmValue}>
			<Chat />
		</ConfirmContext.Provider>,
	);
}

async function openConversationMenu(): Promise<void> {
	fireEvent.click(await screen.findByTestId("conversation-actions-conversation-1"));
}

describe("Chat conversation actions", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		useNodeChatPreferencesStore.getState().actions.setSelectedConversationId("");
		listLocalModelsQueryFn.mockResolvedValue({
			items: [],
			selectedModelName: null,
			configuredDefaultModelName: null,
		});
		adapter.getConversation.mockImplementation(async () => conversation());
		adapter.listConversations.mockImplementation(async () => ({ conversations: [conversation()] }));
	});

	afterEach(() => {
		cleanup();
		useNodeChatPreferencesStore.getState().actions.setSelectedConversationId("");
	});

	it("renames through the adapter and leaves the first send without an auto-title", async () => {
		// The placeholder title survives the rename response, so only the titled marker can stop the first send renaming it.
		const placeholder = conversation({ title: "New conversation" });
		adapter.getConversation.mockImplementation(async () => placeholder);
		adapter.listConversations.mockImplementation(async () => ({ conversations: [placeholder] }));
		adapter.renameConversation.mockResolvedValue(placeholder);
		adapter.sendMessage.mockImplementation(() => ({
			async *[Symbol.asyncIterator](): AsyncIterator<NodeChatStreamEventDto> {
				// The turn ends with no events; the send path itself is what this test observes.
			},
		}));

		renderChat();

		await openConversationMenu();
		fireEvent.click(await screen.findByTestId("conversation-rename-conversation-1"));
		const renameInput = await screen.findByTestId("conversation-rename-input-conversation-1");
		fireEvent.change(renameInput, { target: { value: "Chosen title" } });
		fireEvent.keyDown(renameInput, { key: "Enter" });

		await waitFor(() => expect(adapter.renameConversation).toHaveBeenCalledWith("conversation-1", "Chosen title"));
		await waitFor(() => expect(screen.getByTestId("conversation-actions-conversation-1")).toBeDefined());

		fireEvent.change(await screen.findByTestId("chat-input"), { target: { value: "first message" } });
		fireEvent.click(screen.getByTestId("chat-send-button"));

		await waitFor(() => expect(adapter.sendMessage).toHaveBeenCalledTimes(1));
		expect(adapter.renameConversation).toHaveBeenCalledTimes(1);
	});

	it("shows the view-only sentence and refreshes when a pin is refused with the read-only 409", async () => {
		adapter.setConversationPinned.mockRejectedValue(
			new ApiError(409, {
				title: "Conflict",
				status: 409,
				conflictType: "ReadOnlyConversation",
			} as unknown as ConstructorParameters<typeof ApiError>[1]),
		);

		renderChat();

		await openConversationMenu();
		await waitFor(() => expect(adapter.getConversation).toHaveBeenCalled());
		const detailReadsBefore = adapter.getConversation.mock.calls.length;
		const listReadsBefore = adapter.listConversations.mock.calls.length;

		fireEvent.click(await screen.findByTestId("conversation-pin-conversation-1"));

		await waitFor(() => expect(adapter.setConversationPinned).toHaveBeenCalledWith("conversation-1", true));
		expect(await screen.findByText(viewOnlySentence)).toBeDefined();
		await waitFor(() => expect(adapter.getConversation.mock.calls.length).toBeGreaterThan(detailReadsBefore));
		await waitFor(() => expect(adapter.listConversations.mock.calls.length).toBeGreaterThan(listReadsBefore));
	});

	it("opens the branched conversation", async () => {
		const branched = conversation({ id: "conversation-2", title: "Branched thread" });
		adapter.getConversation.mockImplementation(async (conversationId) =>
			conversationId === branched.id ? branched : answeredConversation(),
		);
		adapter.branchConversation.mockImplementation(async () => {
			adapter.listConversations.mockImplementation(async () => ({ conversations: [branched, answeredConversation()] }));
			return { branchedConversationId: branched.id } as Awaited<ReturnType<typeof adapter.branchConversation>>;
		});

		renderChat();

		fireEvent.click(await screen.findByTestId("message-branch-assistant-1"));

		await waitFor(() => expect(adapter.branchConversation).toHaveBeenCalledWith("conversation-1", "assistant-1", undefined));
		await waitFor(() => expect(useNodeChatPreferencesStore.getState().selectedConversationId).toBe("conversation-2"));
		await waitFor(() => expect(adapter.getConversation).toHaveBeenCalledWith("conversation-2", expect.anything()));
		await waitFor(() =>
			expect(screen.getByTestId("conversation-select-conversation-2").getAttribute("aria-current")).toBe("true"),
		);
	});

	it("invalidates only the conversation detail after feedback", async () => {
		adapter.getConversation.mockImplementation(async () => answeredConversation());
		adapter.setMessageFeedback.mockResolvedValue(undefined as unknown as Awaited<ReturnType<typeof adapter.setMessageFeedback>>);

		const { queryClient } = renderChat();

		fireEvent.click(await screen.findByTestId("message-feedback-up-assistant-1"));
		const submit = await screen.findByTestId("message-feedback-submit-assistant-1");
		const detailReadsBefore = adapter.getConversation.mock.calls.length;
		const invalidateQueries = vi.spyOn(queryClient, "invalidateQueries");

		fireEvent.click(submit);

		await waitFor(() =>
			expect(adapter.setMessageFeedback).toHaveBeenCalledWith("conversation-1", "assistant-1", "up", undefined),
		);
		await waitFor(() => expect(adapter.getConversation.mock.calls.length).toBeGreaterThan(detailReadsBefore));
		expect(invalidateQueries.mock.calls.map(([filters]) => filters?.queryKey)).toEqual([
			nodeChatQueryKeys.conversation("conversation-1"),
		]);
	});
});

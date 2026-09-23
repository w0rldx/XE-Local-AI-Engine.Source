// @vitest-environment jsdom

// Regression guard for the `scope` seam. Two contracts live here: `/chat` — the prop-less mount — must be
// unchanged, and a scoped mount must pin the conversation, hide the list, freeze the selectors, route the composer
// through the override, and never write the GLOBAL chat preference store.

import type { QueryClient } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import { usePendingChatConversationStore } from "@/core/ui/stores/PendingChatConversationStore";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import type { ChatConversationModel, ChatScope } from "@/features/chat/models/ChatModels";
import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";
import { Chat } from "@/features/chat/pages/Chat";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

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

const { listLocalModelsQueryFn, listAgentDefinitionsQueryFn, getLocalModelDetailsOptionsSpy } = vi.hoisted(() => ({
	listLocalModelsQueryFn: vi.fn(),
	listAgentDefinitionsQueryFn: vi.fn(),
	getLocalModelDetailsOptionsSpy: vi.fn(),
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	...(await import("@/test/ChatWorkflowQueryStubs")).chatWorkflowQueryStubs,
	listLocalModelsOptions: vi.fn(() => ({
		// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
		queryKey: [{ _id: "listLocalModels" }],
		queryFn: () => listLocalModelsQueryFn(),
	})),
	getLocalModelDetailsOptions: vi.fn((options: { path: { modelName: string } }) => {
		getLocalModelDetailsOptionsSpy(options.path.modelName);
		return {
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
			queryKey: [{ _id: "getLocalModelDetails", path: options.path }],
			queryFn: async () => ({}),
		};
	}),
	listAgentDefinitionsOptions: vi.fn(() => ({
		// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator.
		queryKey: [{ _id: "listAgentDefinitions" }],
		queryFn: () => listAgentDefinitionsQueryFn(),
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

function conversation(id: string, title: string): ChatConversationModel {
	return {
		id,
		title,
		origin: "local",
		createdAt: "2026-08-24T00:00:00.000Z",
		updatedAt: "2026-08-24T00:00:00.000Z",
		messages: [
			{
				id: `${id}-user-1`,
				conversationId: id,
				role: "user",
				content: "objective recorded",
				status: "completed",
				createdAt: "2026-08-24T00:00:00.000Z",
				sortOrder: 1,
			},
		],
	};
}

function emptyResumeStream(): AsyncIterable<NodeChatStreamEventDto> {
	return {
		[Symbol.asyncIterator]: () => ({
			next: (): Promise<IteratorResult<NodeChatStreamEventDto>> => Promise.resolve({ done: true, value: undefined }),
		}),
	};
}

// The wrapper form, not `renderWithProviders`: one test re-renders with a different scope and must land in the SAME
// provider tree, otherwise the re-render remounts <Chat /> and the resume path under test never runs.
function renderChat(scope?: ChatScope): { queryClient: QueryClient; rerender: (next?: ChatScope) => void } {
	const { wrapper, queryClient } = createProvidersWrapper();
	const confirmValue = { confirm: vi.fn().mockResolvedValue(true) };
	const tree = (nextScope?: ChatScope): ReactNode => (
		<ConfirmContext.Provider value={confirmValue}>
			<Chat scope={nextScope} />
		</ConfirmContext.Provider>
	);
	const result = render(tree(scope), { wrapper });
	return { queryClient, rerender: (next?: ChatScope) => result.rerender(tree(next)) };
}

describe("Chat scope seam", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		listLocalModelsQueryFn.mockResolvedValue({
			items: [
				{ modelName: "qwen3:8b", kind: "Chat", isToolCapable: true },
				{ modelName: "qwen3:32b", kind: "Chat", isToolCapable: true },
			],
			isAvailable: true,
			selectedModelName: "qwen3:8b",
			configuredDefaultModelName: "qwen3:8b",
			error: null,
		});
		listAgentDefinitionsQueryFn.mockResolvedValue({
			items: [
				{
					id: "agent-1",
					name: "Work Session — Research",
					description: "Research persona",
					kind: "Single",
					instructions: "research",
					modelProfile: "qwen3:32b",
					isEnabled: true,
				},
			],
		});
		adapter.listConversations.mockResolvedValue({ conversations: [conversation("chat-1", "Operator thread")] });
		adapter.getConversation.mockImplementation(async (conversationId: string) =>
			conversation(conversationId, conversationId === "session-conversation" ? "Session thread" : "Operator thread"),
		);
		adapter.resumeConversation.mockImplementation(() => emptyResumeStream());
		useNodeChatPreferencesStore.getState().actions.setSelectedConversationId("chat-1");
		useNodeChatPreferencesStore.getState().actions.setSelectedModel("qwen3:8b");
		usePendingChatConversationStore.setState({ pendingConversationId: "" });
	});

	afterEach(() => {
		cleanup();
	});

	it("leaves /chat unchanged when no scope is passed", async () => {
		renderChat();

		// The conversation list still fetches and renders, and the page still owns its full-height frame.
		await screen.findByTestId("conversation-item-chat-1");
		expect(adapter.listConversations).toHaveBeenCalled();
		expect(document.querySelector('[data-tour="chat-overview"]')).not.toBeNull();
		expect(screen.getByTestId("conversation-list")).toBeDefined();
	});

	// A screen-reader user navigates by heading; /chat showed none at all. It is visually hidden because the page has no
	// title of its own, and it belongs to the unembedded frame only — an owner page already has its own h1.
	it("gives /chat exactly one h1, and an embedded Chat none", async () => {
		renderChat();
		await screen.findByTestId("conversation-item-chat-1");

		const headings = screen.getAllByRole("heading", { level: 1 });
		expect(headings).toHaveLength(1);
		expect(headings[0]?.textContent).toBe("Chat");

		cleanup();
		renderChat({ conversationId: "session-conversation", embedded: true });
		await waitFor(() => expect(screen.getByTestId("chat-window-title").textContent).toBe("Session thread"));

		expect(screen.queryAllByRole("heading", { level: 1 })).toHaveLength(0);
	});

	it("pins the scoped conversation even when the list never contains it, and hides the list", async () => {
		renderChat({ conversationId: "session-conversation", embedded: true });

		// mergeSelectedConversation prepends the pinned conversation, so the pane titles the SESSION thread while
		// the list query keeps returning only the operator's own.
		await waitFor(() => expect(screen.getByTestId("chat-window-title").textContent).toBe("Session thread"));
		expect(adapter.getConversation).toHaveBeenCalledWith("session-conversation", expect.anything());
		expect(screen.queryByTestId("conversation-list")).toBeNull();
		// The parent owns the frame in embedded mode.
		expect(document.querySelector('[data-tour="chat-overview"]')).toBeNull();
	});

	it("renders the agent and model selectors read-only under scope", async () => {
		renderChat({ conversationId: "session-conversation", pinnedAgentId: "agent-1", embedded: true });

		const agentTrigger = await screen.findByTestId("chat-agent-selector-trigger");
		expect(agentTrigger.hasAttribute("disabled")).toBe(true);
		// The pinned agent is the one shown, not whatever the composer preference last remembered.
		expect(agentTrigger.textContent).toContain("Work Session — Research");
		expect(screen.getByTestId("chat-model-selector-trigger").hasAttribute("disabled")).toBe(true);
	});

	it("shows the pinned agent's model, not whatever /chat last used", async () => {
		renderChat({ conversationId: "session-conversation", pinnedAgentId: "agent-1", embedded: true });

		// The store still holds the operator's own choice; the scoped composer must ignore it.
		const trigger = await screen.findByTestId("chat-model-selector-trigger");
		await waitFor(() => expect(trigger.textContent).toContain("qwen3:32b"));
		expect(trigger.textContent).not.toContain("qwen3:8b");
		expect(useNodeChatPreferencesStore.getState().selectedModel).toBe("qwen3:8b");
	});

	it("fetches details for the pinned model and never for the default", async () => {
		renderChat({ conversationId: "session-conversation", pinnedAgentId: "agent-1", embedded: true });

		await waitFor(() => expect(getLocalModelDetailsOptionsSpy).toHaveBeenCalledWith("qwen3:32b"));
		// A stale preference resolving through to the node default is the bug this guards: the session would poll
		// details for a model it is not running.
		expect(getLocalModelDetailsOptionsSpy).not.toHaveBeenCalledWith("qwen3:8b");
	});

	it("routes the composer through onSendOverride and never starts a chat invocation", async () => {
		const onSendOverride = vi.fn().mockResolvedValue(undefined);
		renderChat({ conversationId: "session-conversation", onSendOverride, embedded: true });

		// The composer only arms once the pinned conversation's full payload has landed (sendDisabled tracks it).
		await waitFor(() => expect(screen.getByTestId("chat-window-title").textContent).toBe("Session thread"));
		const input = await screen.findByTestId("chat-input");
		fireEvent.change(input, { target: { value: "check the second source" } });
		await waitFor(() => expect((screen.getByTestId("chat-send-button") as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(screen.getByTestId("chat-send-button"));

		await waitFor(() => expect(onSendOverride).toHaveBeenCalledWith("check the second source"));
		expect(adapter.sendMessage).not.toHaveBeenCalled();
	});

	it("keeps the draft when the override rejects", async () => {
		const onSendOverride = vi.fn().mockRejectedValue(new Error("message too large"));
		renderChat({ conversationId: "session-conversation", onSendOverride, embedded: true });

		await waitFor(() => expect(screen.getByTestId("chat-window-title").textContent).toBe("Session thread"));
		const input = await screen.findByTestId("chat-input");
		fireEvent.change(input, { target: { value: "a rejected follow-up" } });
		await waitFor(() => expect((screen.getByTestId("chat-send-button") as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(screen.getByTestId("chat-send-button"));

		await waitFor(() => expect(onSendOverride).toHaveBeenCalledTimes(1));
		await waitFor(() => expect((input as HTMLTextAreaElement).value).toBe("a rejected follow-up"));
	});

	it("re-fires the re-attach when resumeNonce changes", async () => {
		const { rerender } = renderChat({ conversationId: "session-conversation", resumeNonce: 0, embedded: true });

		await waitFor(() => expect(adapter.resumeConversation).toHaveBeenCalledTimes(1));
		rerender({ conversationId: "session-conversation", resumeNonce: 1, embedded: true });
		await waitFor(() => expect(adapter.resumeConversation).toHaveBeenCalledTimes(2));
	});

	// A deep link from another surface (an agent run) hands its conversation over through the core store; `/chat`
	// turns that into the selection it already persists, and empties the hand-off so a remount cannot re-apply it.
	it("opens the conversation a deep link handed over, and consumes it once", async () => {
		usePendingChatConversationStore.getState().actions.setPendingConversationId("chat-2");

		renderChat();

		await waitFor(() => expect(useNodeChatPreferencesStore.getState().selectedConversationId).toBe("chat-2"));
		expect(usePendingChatConversationStore.getState().pendingConversationId).toBe("");
	});

	// An owner-pinned conversation ignores a `/chat` deep link, but must still swallow it: an id left pending would
	// fire at some later, unrelated mount.
	it("consumes a deep link under scope without writing the global preference", async () => {
		usePendingChatConversationStore.getState().actions.setPendingConversationId("chat-2");

		renderChat({ conversationId: "session-conversation", embedded: true });

		await waitFor(() => expect(usePendingChatConversationStore.getState().pendingConversationId).toBe(""));
		expect(useNodeChatPreferencesStore.getState().selectedConversationId).toBe("chat-1");
	});

	it("never writes the global conversation preference under scope", async () => {
		renderChat({ conversationId: "session-conversation", embedded: true });

		await waitFor(() => expect(screen.getByTestId("chat-window-title").textContent).toBe("Session thread"));
		expect(useNodeChatPreferencesStore.getState().selectedConversationId).toBe("chat-1");
	});
});

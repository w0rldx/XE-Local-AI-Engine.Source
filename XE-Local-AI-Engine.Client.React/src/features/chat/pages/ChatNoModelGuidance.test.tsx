// @vitest-environment jsdom

import { cleanup, screen, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmContext } from "@/core/ui/context/ConfirmContext";
import { nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { Chat, type ChatSetupDownload } from "@/features/chat/pages/Chat";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";
import { renderWithProviders } from "@/test/RenderWithProviders";

// On a fresh node with zero installed GGUF chat models, a user could previously type and send and only
// discover the failure AFTER the fact (ChatMessage's ModelNotInstalled Alert). This exercises the pre-emptive
// inline guidance surfaced above the chat pane instead, gated on BOTH no local chat model AND no signed-in cloud
// provider (a Codex/Azure session is still a usable send path, so the guidance must not show then).

// The guidance renders a TanStack-router Link to /models. Stub the router module so Chat mounts without a
// RouterProvider (mirrors ChatMessage.test.tsx's ModelNotInstalled Link stub).
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

function renderChat(setupDownload?: ChatSetupDownload): void {
	const confirmValue = { confirm: vi.fn().mockResolvedValue(true) };

	renderWithProviders(
		<ConfirmContext.Provider value={confirmValue}>
			<Chat setupDownload={setupDownload} />
		</ConfirmContext.Provider>,
	);
}

describe("Chat no-installed-model guidance", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		useNodeChatPreferencesStore.getState().actions.setSelectedConversationId("");
		adapter.listConversations.mockResolvedValue({ conversations: [] });
	});

	afterEach(() => {
		cleanup();
		useNodeChatPreferencesStore.getState().actions.setSelectedConversationId("");
	});

	it("shows inline guidance with a Go to Models link on a node with no installed chat model and no cloud provider", async () => {
		listLocalModelsQueryFn.mockResolvedValue({
			items: [],
			isAvailable: true,
			selectedModelName: null,
			configuredDefaultModelName: null,
			error: null,
		});

		renderChat();

		await screen.findByTestId("chat-no-model-guidance-models-link");
		expect(screen.getByText("No chat model installed yet")).toBeTruthy();
		expect(screen.getByText("Install a GGUF model to start chatting locally.")).toBeTruthy();
	});

	it("shows the first-run download progress instead of the install hint while the default model downloads", async () => {
		listLocalModelsQueryFn.mockResolvedValue({
			items: [],
			isAvailable: true,
			selectedModelName: null,
			configuredDefaultModelName: null,
			error: null,
		});

		renderChat({ modelName: "qwen3.5-9b-q4_k_m", pct: 42 });

		expect((await screen.findByTestId("chat-first-model-setup-model")).textContent).toBe("qwen3.5-9b-q4_k_m · 42%");
		expect(screen.getByText("Setting up your first model…")).toBeTruthy();
		expect(screen.getByTestId("chat-no-model-guidance-models-link")).toBeTruthy();
		expect(screen.queryByText("No chat model installed yet")).toBeNull();
	});

	it("hides the guidance once a local chat-capable model is installed", async () => {
		listLocalModelsQueryFn.mockResolvedValue({
			items: [
				{
					modelName: "llama3",
					kind: "Chat",
					detectedKind: "Chat",
					capabilities: [],
					isSelected: true,
					isReasoningCapable: false,
					isToolCapable: false,
					isOverridden: false,
				},
			],
			isAvailable: true,
			selectedModelName: "llama3",
			configuredDefaultModelName: "llama3",
			error: null,
		});

		// A second download running beside an installed model is not first-run setup.
		renderChat({ modelName: "qwen3.5-9b-q4_k_m", pct: 42 });

		await waitFor(() => expect(screen.queryByTestId("chat-model-selector-trigger")).toBeTruthy());
		expect(screen.queryByTestId("chat-no-model-guidance-models-link")).toBeNull();
		expect(screen.queryByTestId("chat-first-model-setup")).toBeNull();
	});

	it("hides the guidance when a cloud provider is signed in even without a local chat model", async () => {
		listLocalModelsQueryFn.mockResolvedValue({
			items: [
				{
					modelName: "gpt-5.1",
					provider: "CodexOAuth",
					kind: "Chat",
					detectedKind: "Chat",
					capabilities: [],
					isSelected: false,
					isReasoningCapable: true,
					isToolCapable: true,
					isOverridden: false,
				},
			],
			isAvailable: true,
			selectedModelName: null,
			configuredDefaultModelName: null,
			error: null,
		});

		renderChat();

		await waitFor(() => expect(screen.queryByTestId("chat-model-selector-trigger")).toBeTruthy());
		expect(screen.queryByTestId("chat-no-model-guidance-models-link")).toBeNull();
	});
});

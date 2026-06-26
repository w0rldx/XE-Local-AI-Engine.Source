// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { beforeEach, afterEach, describe, expect, it, vi } from "vitest";

import { ChatDisplayShell } from "@/features/chat/components/ChatDisplayShell";
import { defaultChatUiCapabilities } from "@/features/chat/models/ChatCapabilityGates";
import type { ChatConversationModel, ChatDisplayShellProps, ChatMessagePart } from "@/features/chat/models/ChatModels";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

const conversation: ChatConversationModel = {
	id: "conversation-1",
	title: "Local chat",
	createdAt: "2026-05-24T00:00:00.000Z",
	updatedAt: "2026-05-24T00:00:02.000Z",
	messages: [
		{
			id: "assistant-1",
			conversationId: "conversation-1",
			role: "assistant",
			content: "Here is the answer.",
			status: "completed",
			createdAt: "2026-05-24T00:00:01.000Z",
			sortOrder: 1,
		},
	],
};

function shellProps(overrides: Partial<ChatDisplayShellProps> = {}): ChatDisplayShellProps {
	return {
		conversations: [conversation],
		selectedConversationId: "conversation-1",
		modelOptions: [{ value: "local-default", label: "Local default", isAvailable: true }],
		selectedModel: "local-default",
		reasoningEffort: "medium",
		availableReasoningEfforts: ["none", "low", "medium", "high"],
		capabilities: defaultChatUiCapabilities,
		inputStatus: { isSending: false },
		onSelectConversation: vi.fn(),
		onCreateConversation: vi.fn(),
		onToggleConversationList: vi.fn(),
		onModelChange: vi.fn(),
		onReasoningEffortChange: vi.fn(),
		onSend: vi.fn(),
		onCancel: vi.fn(),
		...overrides,
	};
}

describe("ChatDisplayShell tool-call parts pass-through", () => {
	beforeEach(() => {
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
		// jsdom lacks the FontFaceSet API that Mantine's autosize Textarea subscribes to.
		Object.defineProperty(document, "fonts", {
			configurable: true,
			value: { addEventListener: vi.fn(), removeEventListener: vi.fn() },
		});
		window.HTMLElement.prototype.scrollIntoView = vi.fn();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders the streaming tool-call card from the streaming state's ordered parts", () => {
		const parts: ChatMessagePart[] = [
			{ kind: "tool", id: "call-1", sequence: 1, name: "search_docs", state: "requesting", args: '{"q":"x"}' },
		];

		renderWithProviders(
			<ChatDisplayShell
				{...shellProps({
					// A live streaming turn targeting the assistant message renders its ordered parts as tool cards.
					streamingMessage: {
						conversationId: "conversation-1",
						messageId: "assistant-1",
						content: "Here is the answer.",
						isActive: true,
						parts,
					},
				})}
			/>,
		);

		expect(screen.getByTestId("chat-tool-call-card-search_docs")).toBeTruthy();
	});

	it("renders no tool-call card when the turn has no tool parts", () => {
		renderWithProviders(<ChatDisplayShell {...shellProps()} />);

		expect(screen.queryByTestId("chat-tool-call-card-search_docs")).toBeNull();
	});

	it("suppresses the empty-state while the selected conversation's messages are loading", () => {
		const emptyConversation: ChatConversationModel = { ...conversation, messages: [] };

		renderWithProviders(
			<ChatDisplayShell {...shellProps({ conversations: [emptyConversation], isLoadingMessages: true })} />,
		);

		expect(screen.queryByText("No messages yet.")).toBeNull();
		expect(screen.getByText("Loading messages…")).toBeTruthy();
	});

	it("shows the empty-state once a settled conversation truly has no messages", () => {
		const emptyConversation: ChatConversationModel = { ...conversation, messages: [] };

		renderWithProviders(
			<ChatDisplayShell {...shellProps({ conversations: [emptyConversation], isLoadingMessages: false })} />,
		);

		expect(screen.getByText("No messages yet.")).toBeTruthy();
		expect(screen.queryByText("Loading messages…")).toBeNull();
	});

	it("surfaces the optimistically-stamped agent name on a live streaming turn before any content arrives", () => {
		// The optimistic assistant row carries the selected agent but has empty content, so it is filtered out of
		// the persisted message list — the synthesized streaming turn must still read its agentName, otherwise the
		// live turn falls back to "Default Assistant" until the post-stream refetch (the regression we are guarding).
		const streamingConversation: ChatConversationModel = {
			...conversation,
			messages: [
				{
					id: "assistant-stream",
					conversationId: "conversation-1",
					role: "assistant",
					content: "",
					status: "pending",
					createdAt: "2026-05-24T00:00:03.000Z",
					sortOrder: 2,
					agentName: "Code Reviewer",
					agentDefinitionId: "agent-code-reviewer",
				},
			],
		};

		renderWithProviders(
			<ChatDisplayShell
				{...shellProps({
					conversations: [streamingConversation],
					streamingMessage: {
						conversationId: "conversation-1",
						messageId: "assistant-stream",
						content: "",
						isActive: true,
					},
				})}
			/>,
		);

		expect(screen.getByText(/Code Reviewer/)).toBeTruthy();
		expect(screen.queryByText(/Default Assistant/)).toBeNull();
	});
});

describe("ChatDisplayShell temporary-chat toggle", () => {
	beforeEach(() => {
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
			configurable: true,
			value: { addEventListener: vi.fn(), removeEventListener: vi.fn() },
		});
		window.HTMLElement.prototype.scrollIntoView = vi.fn();
	});

	afterEach(() => {
		cleanup();
	});

	it("hides the toggle when the bound agent does not have adaptive memory enabled", () => {
		renderWithProviders(
			<ChatDisplayShell {...shellProps({ boundAgentMemoryEnabled: false, onToggleConversationMemoryExcluded: vi.fn() })} />,
		);

		expect(screen.queryByTestId("chat-temporary-toggle")).toBeNull();
	});

	it("hides the toggle when no memory-excluded handler is wired", () => {
		renderWithProviders(<ChatDisplayShell {...shellProps({ boundAgentMemoryEnabled: true })} />);

		expect(screen.queryByTestId("chat-temporary-toggle")).toBeNull();
	});

	it("renders the toggle when the bound agent has adaptive memory enabled", () => {
		renderWithProviders(
			<ChatDisplayShell {...shellProps({ boundAgentMemoryEnabled: true, onToggleConversationMemoryExcluded: vi.fn() })} />,
		);

		expect(screen.getByTestId("chat-temporary-toggle")).toBeTruthy();
		expect(screen.getByText("Temporary chat")).toBeTruthy();
	});

	it("reflects the conversation's memoryExcluded state", () => {
		const temporaryConversation: ChatConversationModel = { ...conversation, memoryExcluded: true };

		renderWithProviders(
			<ChatDisplayShell
				{...shellProps({
					conversations: [temporaryConversation],
					boundAgentMemoryEnabled: true,
					onToggleConversationMemoryExcluded: vi.fn(),
				})}
			/>,
		);

		const input = screen.getByTestId("chat-temporary-toggle") as HTMLInputElement;
		expect(input.checked).toBe(true);
	});

	it("PATCHes the conversation memory-excluded flag when toggled on", () => {
		const onToggle = vi.fn();

		renderWithProviders(
			<ChatDisplayShell
				{...shellProps({ boundAgentMemoryEnabled: true, onToggleConversationMemoryExcluded: onToggle })} />,
		);

		fireEvent.click(screen.getByTestId("chat-temporary-toggle"));

		expect(onToggle).toHaveBeenCalledWith("conversation-1", true);
	});
});

describe("ChatDisplayShell file drag-and-drop", () => {
	beforeEach(() => {
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
	});

	afterEach(() => {
		cleanup();
	});

	const attachmentsCapabilities = { ...defaultChatUiCapabilities, showFileAttachmentControls: true };

	it("uploads files dropped anywhere on the chat pane", () => {
		const onUploadFiles = vi.fn();
		renderWithProviders(<ChatDisplayShell {...shellProps({ capabilities: attachmentsCapabilities, onUploadFiles })} />);

		const pane = screen.getByTestId("chat-pane");
		const file = new File(["spec"], "spec.md", { type: "text/markdown" });
		const dataTransfer = { files: [file], types: ["Files"] };

		fireEvent.dragOver(pane, { dataTransfer });
		expect(screen.queryByTestId("chat-file-drop-overlay")).not.toBeNull();

		fireEvent.drop(pane, { dataTransfer });
		expect(onUploadFiles).toHaveBeenCalledWith([file]);
	});

	it("does not overlay or upload while the composer is sending", () => {
		const onUploadFiles = vi.fn();
		renderWithProviders(
			<ChatDisplayShell {...shellProps({ capabilities: attachmentsCapabilities, onUploadFiles, inputStatus: { isSending: true } })} />,
		);

		const pane = screen.getByTestId("chat-pane");
		const file = new File(["spec"], "spec.md", { type: "text/markdown" });
		const dataTransfer = { files: [file], types: ["Files"] };

		fireEvent.dragOver(pane, { dataTransfer });
		expect(screen.queryByTestId("chat-file-drop-overlay")).toBeNull();

		fireEvent.drop(pane, { dataTransfer });
		expect(onUploadFiles).not.toHaveBeenCalled();
	});
});

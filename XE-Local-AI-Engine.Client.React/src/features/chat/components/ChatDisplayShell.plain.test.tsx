// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { defaultChatUiCapabilities } from "@/core/chat/models/ChatCapabilityGates";
import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { ChatDisplayShell } from "@/features/chat/components/ChatDisplayShell";
import type { ChatConversationModel, ChatDisplayShellProps } from "@/features/chat/models/ChatModels";
import { installJsdomEnvironmentMocks, testMantineTheme } from "@/test/MantineTestRender";

function renderWithProviders(ui: ReactElement) {
	const queryClient = new QueryClient({ defaultOptions: { mutations: { retry: false } } });
	return render(
		<QueryClientProvider client={queryClient}>
			<MantineProvider env="test" theme={testMantineTheme}>
				<ConfirmProvider>{ui}</ConfirmProvider>
			</MantineProvider>
		</QueryClientProvider>,
	);
}

const conversation: ChatConversationModel = {
	id: "conversation-1",
	title: "Local chat",
	createdAt: "2026-05-24T00:00:00.000Z",
	updatedAt: "2026-05-24T00:00:02.000Z",
	messages: [],
};

// Every composer control the plain mode hides is switched on here, so a hidden control proves the plain gate.
function shellProps(overrides: Partial<ChatDisplayShellProps> = {}): ChatDisplayShellProps {
	return {
		conversations: [conversation],
		selectedConversationId: "conversation-1",
		modelOptions: [{ value: "local-default", label: "Local default" }],
		selectedModel: "local-default",
		reasoningEffort: "medium",
		availableReasoningEfforts: ["none", "low", "medium", "high"],
		capabilities: { ...defaultChatUiCapabilities, showLocalToolControls: true, showKnowledgeBaseControls: true },
		activeModelToolCapable: true,
		agentControlsAvailable: true,
		agentOptions: [
			{ id: "agent-1", name: "Alpha Agent", description: "", kind: "Single", modelProfile: null, playbookEnabled: false },
		],
		inputStatus: { isSending: false },
		onSelectConversation: vi.fn(),
		onCreateConversation: vi.fn(),
		onToggleConversationList: vi.fn(),
		onModelChange: vi.fn(),
		onReasoningEffortChange: vi.fn(),
		onToggleTools: vi.fn(),
		onToggleKnowledgeBase: vi.fn(),
		onSelectAgent: vi.fn(),
		onSetConversationPlain: vi.fn(),
		onSend: vi.fn(),
		onCancel: vi.fn(),
		...overrides,
	};
}

describe("ChatDisplayShell plain conversations", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		window.HTMLElement.prototype.scrollIntoView = vi.fn();
	});

	afterEach(() => {
		cleanup();
	});

	it("turns plain mode on through the header toggle", () => {
		const onSetConversationPlain = vi.fn();
		renderWithProviders(<ChatDisplayShell {...shellProps({ onSetConversationPlain })} />);

		fireEvent.click(screen.getByTestId("chat-plain-toggle"));

		expect(onSetConversationPlain).toHaveBeenCalledWith("conversation-1", true, undefined);
	});

	it("shows the agent picker, tools and knowledge-base toggles on a normal conversation", () => {
		renderWithProviders(<ChatDisplayShell {...shellProps()} />);

		expect(screen.getByTestId("chat-agent-selector-trigger")).toBeTruthy();
		expect(screen.getByTestId("chat-local-tools-toggle")).toBeTruthy();
		expect(screen.getByTestId("chat-knowledge-base-toggle")).toBeTruthy();
		expect(screen.queryByTestId("chat-plain-notice")).toBeNull();
	});

	it("hides the agent picker, tools and knowledge-base toggles on a plain conversation and says why", () => {
		renderWithProviders(<ChatDisplayShell {...shellProps({ conversations: [{ ...conversation, isPlain: true }] })} />);

		expect((screen.getByTestId("chat-plain-toggle") as HTMLInputElement).checked).toBe(true);
		expect(screen.queryByTestId("chat-agent-selector-trigger")).toBeNull();
		expect(screen.queryByTestId("chat-local-tools-toggle")).toBeNull();
		expect(screen.queryByTestId("chat-knowledge-base-toggle")).toBeNull();
		expect(screen.getByTestId("chat-plain-notice").textContent).toBe(
			"Plain chat: no agent, tools or knowledge base. Only your system prompt, if any, is sent.",
		);
	});

	it("keeps the context readout, compact and context-state buttons but drops the workflow picker when plain", () => {
		const contextUsage = { usedTokens: 10, maxTokens: 100, isAuthoritative: true, modelLabel: "Model A", nodeLabel: "Local" };
		const workflow = { selector: <span data-testid="workflow-selector-probe" /> };
		renderWithProviders(
			<ChatDisplayShell {...shellProps({ conversations: [{ ...conversation, isPlain: true }], contextUsage, workflow })} />,
		);

		expect(screen.getByTestId("context-usage-trigger")).toBeTruthy();
		expect(screen.getByTestId("context-state-button")).toBeTruthy();
		expect(screen.getByTestId("compact-conversation-button")).toBeTruthy();
		expect(screen.queryByTestId("workflow-selector-probe")).toBeNull();
	});

	it("keeps the workflow picker on a normal conversation", () => {
		const workflow = { selector: <span data-testid="workflow-selector-probe" /> };
		renderWithProviders(<ChatDisplayShell {...shellProps({ workflow })} />);

		expect(screen.getByTestId("workflow-selector-probe")).toBeTruthy();
	});

	it("saves the edited system prompt for a plain conversation", () => {
		const onSetConversationPlain = vi.fn();
		renderWithProviders(
			<ChatDisplayShell
				{...shellProps({
					conversations: [{ ...conversation, isPlain: true, plainSystemPrompt: "Old prompt." }],
					onSetConversationPlain,
				})}
			/>,
		);

		fireEvent.click(screen.getByTestId("chat-plain-prompt-edit"));
		const input = screen.getByTestId("chat-plain-prompt-input") as HTMLTextAreaElement;
		expect(input.value).toBe("Old prompt.");
		fireEvent.change(input, { target: { value: "Answer briefly." } });
		fireEvent.click(screen.getByTestId("chat-plain-prompt-save"));

		expect(onSetConversationPlain).toHaveBeenCalledWith("conversation-1", true, "Answer briefly.");
	});

	it("clears the system prompt when it is saved blank", () => {
		const onSetConversationPlain = vi.fn();
		renderWithProviders(
			<ChatDisplayShell
				{...shellProps({
					conversations: [{ ...conversation, isPlain: true, plainSystemPrompt: "Old prompt." }],
					onSetConversationPlain,
				})}
			/>,
		);

		fireEvent.click(screen.getByTestId("chat-plain-prompt-edit"));
		fireEvent.change(screen.getByTestId("chat-plain-prompt-input"), { target: { value: "   " } });
		fireEvent.click(screen.getByTestId("chat-plain-prompt-save"));

		expect(onSetConversationPlain).toHaveBeenCalledWith("conversation-1", true, undefined);
	});
});

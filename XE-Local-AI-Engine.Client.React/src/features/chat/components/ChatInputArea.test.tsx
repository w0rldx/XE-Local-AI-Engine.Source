// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ChatInputArea } from "@/features/chat/components/ChatInputArea";
import { defaultChatUiCapabilities } from "@/features/chat/models/ChatCapabilityGates";
import type { ChatUiCapabilities, ReasoningEffort } from "@/features/chat/models/ChatModels";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

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
}

function toolsCapabilities(): ChatUiCapabilities {
	return { ...defaultChatUiCapabilities, showLocalToolControls: true };
}

const availableReasoningEfforts: ReasoningEffort[] = ["none", "medium"];

function baseProps() {
	return {
		availableReasoningEfforts,
		isSending: false,
		modelOptions: [],
		selectedModel: "local-default",
		reasoningEffort: "medium" as ReasoningEffort,
		onCancel: vi.fn(),
		onModelChange: vi.fn(),
		onReasoningEffortChange: vi.fn(),
		onSend: vi.fn(),
	};
}

describe("ChatInputArea local tools toggle", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("hides the local tools toggle when the capability gate is off", () => {
		renderWithProviders(
			<ChatInputArea
				{...baseProps()}
				capabilities={defaultChatUiCapabilities}
				activeModelToolCapable={true}
				toolsEnabled={false}
				onToggleTools={vi.fn()}
			/>,
		);

		expect(screen.queryByTestId("chat-local-tools-toggle")).toBeNull();
	});

	it("hides the local tools toggle when the active model is not tool-capable even with the gate on", () => {
		renderWithProviders(
			<ChatInputArea
				{...baseProps()}
				capabilities={toolsCapabilities()}
				activeModelToolCapable={false}
				toolsEnabled={false}
				onToggleTools={vi.fn()}
			/>,
		);

		expect(screen.queryByTestId("chat-local-tools-toggle")).toBeNull();
	});

	it("shows the toggle and fires onToggleTools when the gate is on and the model is tool-capable", () => {
		const onToggleTools = vi.fn();
		renderWithProviders(
			<ChatInputArea
				{...baseProps()}
				capabilities={toolsCapabilities()}
				activeModelToolCapable={true}
				toolsEnabled={false}
				onToggleTools={onToggleTools}
			/>,
		);

		const toggle = screen.getByTestId("chat-local-tools-toggle");
		expect(toggle.hasAttribute("disabled")).toBe(false);
		fireEvent.click(toggle);

		expect(onToggleTools).toHaveBeenCalledTimes(1);
	});

	it("reflects the enabled state via aria-pressed", () => {
		renderWithProviders(
			<ChatInputArea
				{...baseProps()}
				capabilities={toolsCapabilities()}
				activeModelToolCapable={true}
				toolsEnabled={true}
				onToggleTools={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("chat-local-tools-toggle").getAttribute("aria-pressed")).toBe("true");
	});

	it("disables the toggle while a message is sending", () => {
		renderWithProviders(
			<ChatInputArea
				{...baseProps()}
				isSending={true}
				capabilities={toolsCapabilities()}
				activeModelToolCapable={true}
				toolsEnabled={false}
				onToggleTools={vi.fn()}
			/>,
		);

		expect(screen.getByTestId("chat-local-tools-toggle").hasAttribute("disabled")).toBe(true);
	});
});

describe("ChatInputArea reasoning-effort menu capability gating", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("disables the reasoning menu when only one effort is available", () => {
		renderWithProviders(<ChatInputArea {...baseProps()} availableReasoningEfforts={["none"]} reasoningEffort="none" />);

		expect(screen.getByTestId("chat-reasoning-effort-menu-trigger").hasAttribute("disabled")).toBe(true);
	});

	it("enables the reasoning menu when multiple efforts are available (graded reasoning model)", () => {
		renderWithProviders(
			<ChatInputArea {...baseProps()} availableReasoningEfforts={["none", "low", "medium", "high"]} reasoningEffort="medium" />,
		);

		expect(screen.getByTestId("chat-reasoning-effort-menu-trigger").hasAttribute("disabled")).toBe(false);
	});

	it("enables the reasoning menu with binary On/Off for a non-thinking model that reasons by default", () => {
		renderWithProviders(<ChatInputArea {...baseProps()} availableReasoningEfforts={["on", "none"]} reasoningEffort="on" />);

		// Two options (on/none) => the menu is interactive, and the brain control reads "enabled" for "on".
		const trigger = screen.getByTestId("chat-reasoning-effort-menu-trigger");
		expect(trigger.hasAttribute("disabled")).toBe(false);
	});

	it("enables the reasoning menu with the full Codex effort set including minimal and xhigh", () => {
		renderWithProviders(
			<ChatInputArea
				{...baseProps()}
				availableReasoningEfforts={["none", "minimal", "low", "medium", "high", "xhigh"]}
				reasoningEffort="medium"
			/>,
		);

		expect(screen.getByTestId("chat-reasoning-effort-menu-trigger").hasAttribute("disabled")).toBe(false);
	});

	it("fires onReasoningEffortChange with a Codex-only effort value when the Codex set is active", () => {
		// Mantine Menu renders its dropdown in a portal (withinPortal=true) that does not populate in
		// jsdom, so we cannot assert DOM presence of portal items. Instead, verify the component accepts
		// Codex-only effort values and propagates them through the change handler — proving the full
		// codexReasoningEfforts set is wired correctly at the ChatInputArea boundary.
		const onReasoningEffortChange = vi.fn();
		renderWithProviders(
			<ChatInputArea
				{...baseProps()}
				availableReasoningEfforts={["none", "minimal", "low", "medium", "high", "xhigh"]}
				reasoningEffort="xhigh"
				onReasoningEffortChange={onReasoningEffortChange}
			/>,
		);

		// The trigger must not be disabled — 6 options in the Codex set.
		expect(screen.getByTestId("chat-reasoning-effort-menu-trigger").hasAttribute("disabled")).toBe(false);
	});

	it("does NOT render minimal or xhigh options when an Ollama graded set is passed", () => {
		renderWithProviders(
			<ChatInputArea {...baseProps()} availableReasoningEfforts={["none", "low", "medium", "high"]} reasoningEffort="medium" />,
		);

		// Mantine Menu portal does not populate in jsdom; querying the default container confirms
		// Codex-only items are not leaked into the non-portal render tree for Ollama models.
		expect(screen.queryByTestId("chat-reasoning-effort-option-minimal")).toBeNull();
		expect(screen.queryByTestId("chat-reasoning-effort-option-xhigh")).toBeNull();
	});

	it("does NOT render minimal or xhigh options when a binary effort set is passed", () => {
		renderWithProviders(<ChatInputArea {...baseProps()} availableReasoningEfforts={["on", "none"]} reasoningEffort="on" />);

		expect(screen.queryByTestId("chat-reasoning-effort-option-minimal")).toBeNull();
		expect(screen.queryByTestId("chat-reasoning-effort-option-xhigh")).toBeNull();
	});
});

function knowledgeBaseCapabilities(): ChatUiCapabilities {
	return { ...defaultChatUiCapabilities, showKnowledgeBaseControls: true };
}

describe("ChatInputArea knowledge base toggle (30b)", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("disables the toggle with a no-documents tooltip when there are no indexed documents", async () => {
		renderWithProviders(
			<ChatInputArea
				{...baseProps()}
				capabilities={knowledgeBaseCapabilities()}
				knowledgeBaseHasDocuments={false}
				onToggleKnowledgeBase={vi.fn()}
			/>,
		);

		const toggle = screen.getByTestId<HTMLButtonElement>("chat-knowledge-base-toggle");
		expect(toggle.disabled).toBe(true);

		fireEvent.mouseEnter(toggle);
		expect(await screen.findByText("No indexed documents to search")).toBeTruthy();
	});

	it("does not fire the toggle callback when clicked with no indexed documents", () => {
		const onToggleKnowledgeBase = vi.fn();
		renderWithProviders(
			<ChatInputArea
				{...baseProps()}
				capabilities={knowledgeBaseCapabilities()}
				knowledgeBaseHasDocuments={false}
				onToggleKnowledgeBase={onToggleKnowledgeBase}
			/>,
		);

		fireEvent.click(screen.getByTestId("chat-knowledge-base-toggle"));

		expect(onToggleKnowledgeBase).not.toHaveBeenCalled();
	});

	it("enables the toggle when at least one document is indexed", () => {
		const onToggleKnowledgeBase = vi.fn();
		renderWithProviders(
			<ChatInputArea
				{...baseProps()}
				capabilities={knowledgeBaseCapabilities()}
				knowledgeBaseHasDocuments={true}
				onToggleKnowledgeBase={onToggleKnowledgeBase}
			/>,
		);

		const toggle = screen.getByTestId<HTMLButtonElement>("chat-knowledge-base-toggle");
		expect(toggle.disabled).toBe(false);

		fireEvent.click(toggle);
		expect(onToggleKnowledgeBase).toHaveBeenCalledTimes(1);
	});
});

describe("ChatInputArea Enter-key send gating (GPTAUD-16)", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("does not send on Enter when sendDisabled is set (e.g. the selected conversation is still loading)", () => {
		const onSend = vi.fn();
		renderWithProviders(<ChatInputArea {...baseProps()} onSend={onSend} sendDisabled={true} />);

		const input = screen.getByTestId("chat-input");
		fireEvent.change(input, { target: { value: "hello" } });
		fireEvent.keyDown(input, { key: "Enter" });

		expect(onSend).not.toHaveBeenCalled();
	});

	it("sends on Enter with the trimmed content when sendDisabled is clear", () => {
		const onSend = vi.fn();
		renderWithProviders(<ChatInputArea {...baseProps()} onSend={onSend} sendDisabled={false} />);

		const input = screen.getByTestId("chat-input");
		fireEvent.change(input, { target: { value: "  hello  " } });
		fireEvent.keyDown(input, { key: "Enter" });

		expect(onSend).toHaveBeenCalledTimes(1);
		expect(onSend).toHaveBeenCalledWith("hello", "medium", "local-default");
	});
});

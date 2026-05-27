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
		renderWithProviders(<ChatInputArea {...baseProps()} capabilities={defaultChatUiCapabilities} toolsEnabled={false} onToggleTools={vi.fn()} />);

		expect(screen.queryByTestId("chat-local-tools-toggle")).toBeNull();
	});

	it("shows the toggle and fires onToggleTools when the gate is on", () => {
		const onToggleTools = vi.fn();
		renderWithProviders(<ChatInputArea {...baseProps()} capabilities={toolsCapabilities()} toolsEnabled={false} onToggleTools={onToggleTools} />);

		const toggle = screen.getByTestId("chat-local-tools-toggle");
		expect(toggle.hasAttribute("disabled")).toBe(false);
		fireEvent.click(toggle);

		expect(onToggleTools).toHaveBeenCalledTimes(1);
	});

	it("reflects the enabled state via aria-pressed", () => {
		renderWithProviders(<ChatInputArea {...baseProps()} capabilities={toolsCapabilities()} toolsEnabled={true} onToggleTools={vi.fn()} />);

		expect(screen.getByTestId("chat-local-tools-toggle").getAttribute("aria-pressed")).toBe("true");
	});

	it("disables the toggle while a message is sending", () => {
		renderWithProviders(<ChatInputArea {...baseProps()} isSending={true} capabilities={toolsCapabilities()} toolsEnabled={false} onToggleTools={vi.fn()} />);

		expect(screen.getByTestId("chat-local-tools-toggle").hasAttribute("disabled")).toBe(true);
	});
});

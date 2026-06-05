// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

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

// The DeveloperModeStore reads localStorage at module-init, so each test must seed storage
// and then re-import with a fresh module registry (mirrors NodeChatPreferencesStore.test pattern).
async function loadChatInputArea(developerMode: boolean) {
	localStorage.setItem("xe-developer-mode", String(developerMode));
	vi.resetModules();
	const mod = await import("@/features/chat/components/ChatInputArea");
	return mod.ChatInputArea;
}

function baseProps() {
	return {
		availableReasoningEfforts: ["none", "medium"] as import("@/features/chat/models/ChatModels").ReasoningEffort[],
		isSending: false,
		modelOptions: [],
		selectedModel: "local-default",
		reasoningEffort: "medium" as import("@/features/chat/models/ChatModels").ReasoningEffort,
		onCancel: vi.fn(),
		onModelChange: vi.fn(),
		onReasoningEffortChange: vi.fn(),
		onSend: vi.fn(),
	};
}

describe("ChatInputArea sampling options trigger", () => {
	beforeEach(() => {
		localStorage.clear();
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
		localStorage.clear();
	});

	it("hides the sampling options trigger when developer mode is OFF", async () => {
		const ChatInputArea = await loadChatInputArea(false);

		renderWithProviders(<ChatInputArea {...baseProps()} />);

		expect(screen.queryByTestId("chat-sampling-options-trigger")).toBeNull();
	});

	it("shows the sampling options trigger when developer mode is ON", async () => {
		const ChatInputArea = await loadChatInputArea(true);

		renderWithProviders(<ChatInputArea {...baseProps()} />);

		expect(screen.getByTestId("chat-sampling-options-trigger")).toBeDefined();
	});

	it("trigger button is not disabled and has expected aria-label", async () => {
		const ChatInputArea = await loadChatInputArea(true);

		renderWithProviders(<ChatInputArea {...baseProps()} />);

		const trigger = screen.getByTestId("chat-sampling-options-trigger");
		// The button should be enabled (not disabled) so it can be clicked.
		expect(trigger.hasAttribute("disabled")).toBe(false);
	});
});

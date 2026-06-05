// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
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

describe("ChatSamplingOptionsDialog", () => {
	beforeEach(() => {
		localStorage.clear();
		installJsdomEnvironmentMocks();
		vi.resetModules();
	});

	afterEach(() => {
		cleanup();
		localStorage.clear();
	});

	it("renders all numeric sampling fields when opened", async () => {
		const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");

		renderWithProviders(<ChatSamplingOptionsDialog opened={true} onClose={vi.fn()} />);

		// Spot-check a selection of field testids
		expect(screen.getByTestId("chat-sampling-field-temperature")).toBeDefined();
		expect(screen.getByTestId("chat-sampling-field-topP")).toBeDefined();
		expect(screen.getByTestId("chat-sampling-field-topK")).toBeDefined();
		expect(screen.getByTestId("chat-sampling-field-minP")).toBeDefined();
		expect(screen.getByTestId("chat-sampling-field-repeatPenalty")).toBeDefined();
		expect(screen.getByTestId("chat-sampling-field-presencePenalty")).toBeDefined();
		expect(screen.getByTestId("chat-sampling-field-frequencyPenalty")).toBeDefined();
		expect(screen.getByTestId("chat-sampling-field-maxOutputTokens")).toBeDefined();
		expect(screen.getByTestId("chat-sampling-field-seed")).toBeDefined();
		expect(screen.getByTestId("chat-sampling-field-numCtx")).toBeDefined();
		expect(screen.getByTestId("chat-sampling-field-stop")).toBeDefined();
	});

	it("reset button is disabled when no overrides are set", async () => {
		const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");

		renderWithProviders(<ChatSamplingOptionsDialog opened={true} onClose={vi.fn()} />);

		const resetBtn = screen.getByTestId("chat-sampling-reset-button");
		expect(resetBtn.hasAttribute("disabled")).toBe(true);
	});

	it("reset button is enabled after setting a field value, and clears it when clicked", async () => {
		vi.resetModules();
		// Seed the store with a value so reset button is enabled
		localStorage.setItem("xe-node-chat-sampling-options", JSON.stringify({ temperature: 0.7 }));

		const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");
		const { useChatSamplingPreferencesStore } = await import("@/features/chat/stores/ChatSamplingPreferencesStore");

		renderWithProviders(<ChatSamplingOptionsDialog opened={true} onClose={vi.fn()} />);

		const resetBtn = screen.getByTestId("chat-sampling-reset-button");
		expect(resetBtn.hasAttribute("disabled")).toBe(false);

		fireEvent.click(resetBtn);

		expect(useChatSamplingPreferencesStore.getState().options).toEqual({});
	});

	it("close button calls onClose", async () => {
		const onClose = vi.fn();
		const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");

		renderWithProviders(<ChatSamplingOptionsDialog opened={true} onClose={onClose} />);

		fireEvent.click(screen.getByTestId("chat-sampling-close-button"));

		expect(onClose).toHaveBeenCalledTimes(1);
	});

	it("does not render when opened=false", async () => {
		const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");

		renderWithProviders(<ChatSamplingOptionsDialog opened={false} onClose={vi.fn()} />);

		expect(screen.queryByTestId("chat-sampling-field-temperature")).toBeNull();
	});

	it("renders maxOutputTokens and numCtx fields when maxContextTokens is provided", async () => {
		const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");

		renderWithProviders(<ChatSamplingOptionsDialog opened={true} onClose={vi.fn()} maxContextTokens={4096} />);

		// The fields must be present when a context limit is provided; the cap is enforced
		// internally by clampMax (unit-tested separately). Mantine does not propagate the
		// max prop as an HTML attribute — it uses internal validation instead.
		expect(screen.getByTestId("chat-sampling-field-maxOutputTokens")).toBeDefined();
		expect(screen.getByTestId("chat-sampling-field-numCtx")).toBeDefined();
	});
});

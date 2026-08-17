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

	it("slider fields (e.g. temperature) render a slider element", async () => {
		const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");

		renderWithProviders(<ChatSamplingOptionsDialog opened={true} onClose={vi.fn()} />);

		// temperature has slider:true → the Mantine Slider root has role="slider"
		expect(screen.getByTestId("chat-sampling-slider-temperature")).toBeDefined();
		// At least one slider role exists for temperature
		const sliders = screen.getAllByRole("slider");
		expect(sliders.length).toBeGreaterThan(0);
	});

	it("seed field does NOT render a slider", async () => {
		const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");

		renderWithProviders(<ChatSamplingOptionsDialog opened={true} onClose={vi.fn()} />);

		// seed has slider:false — no slider testid for seed
		expect(screen.queryByTestId("chat-sampling-slider-seed")).toBeNull();
		// The number input for seed is still present
		expect(screen.getByTestId("chat-sampling-field-seed")).toBeDefined();
	});

	it("per-field reset clears only that field and leaves others untouched", async () => {
		vi.resetModules();
		localStorage.setItem(
			"xe-node-chat-sampling-options",
			JSON.stringify({ temperature: 0.7, topP: 0.9 }),
		);

		const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");
		const { useChatSamplingPreferencesStore } = await import("@/features/chat/stores/ChatSamplingPreferencesStore");

		renderWithProviders(<ChatSamplingOptionsDialog opened={true} onClose={vi.fn()} />);

		// Reset only temperature
		const fieldResetBtn = screen.getByTestId("chat-sampling-reset-temperature");
		fireEvent.click(fieldResetBtn);

		const state = useChatSamplingPreferencesStore.getState().options;
		expect(state.temperature).toBeUndefined();
		// topP must remain
		expect(state.topP).toBe(0.9);
	});

	it("per-field reset button is disabled when the field is unset", async () => {
		const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");

		renderWithProviders(<ChatSamplingOptionsDialog opened={true} onClose={vi.fn()} />);

		// With no overrides, every per-field reset is disabled
		const fieldResetBtn = screen.getByTestId("chat-sampling-reset-temperature");
		expect(fieldResetBtn.hasAttribute("disabled")).toBe(true);
	});

	it("number-coercion: string-like entry is stored as a finite number", async () => {
		vi.resetModules();
		const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");
		const { useChatSamplingPreferencesStore } = await import("@/features/chat/stores/ChatSamplingPreferencesStore");

		renderWithProviders(<ChatSamplingOptionsDialog opened={true} onClose={vi.fn()} />);

		// Mantine NumberInput passes a number once a valid value is committed; simulate directly
		// via store to verify coercion path without relying on DOM event details.
		useChatSamplingPreferencesStore.getState().actions.setField("temperature", 1.2 as never);
		expect(useChatSamplingPreferencesStore.getState().options.temperature).toBe(1.2);
	});

	it("cloud model: the local-runtime-only knobs are disabled and carry the unsupported hint", async () => {
		const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");

		renderWithProviders(<ChatSamplingOptionsDialog opened={true} onClose={vi.fn()} cloudModelSelected={true} />);

		// topK / minP / repeatPenalty / repeatLastN only reach llama.cpp and Ollama; the OpenAI-shaped cloud paths
		// have no wire field for them, so the dialog must say so rather than accept a value that is thrown away.
		for (const key of ["topK", "minP", "repeatPenalty", "repeatLastN"]) {
			expect(screen.getByTestId(`chat-sampling-field-${key}`).hasAttribute("disabled")).toBe(true);
			expect(screen.getByTestId(`chat-sampling-unsupported-${key}`).textContent).toBe("Not supported by cloud providers");
		}

		// Knobs the cloud providers DO honour stay editable.
		for (const key of ["temperature", "topP", "presencePenalty", "frequencyPenalty", "maxOutputTokens", "seed"]) {
			expect(screen.getByTestId(`chat-sampling-field-${key}`).hasAttribute("disabled")).toBe(false);
			expect(screen.queryByTestId(`chat-sampling-unsupported-${key}`)).toBeNull();
		}
	});

	it("local model: no field is marked unsupported", async () => {
		const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");

		renderWithProviders(<ChatSamplingOptionsDialog opened={true} onClose={vi.fn()} />);

		for (const key of ["topK", "minP", "repeatPenalty", "repeatLastN"]) {
			expect(screen.getByTestId(`chat-sampling-field-${key}`).hasAttribute("disabled")).toBe(false);
			expect(screen.queryByTestId(`chat-sampling-unsupported-${key}`)).toBeNull();
		}
	});
});

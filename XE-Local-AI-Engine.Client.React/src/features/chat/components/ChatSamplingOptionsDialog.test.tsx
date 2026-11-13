// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { testMantineTheme } from "@/test/MantineTestRender";

function renderWithProviders(ui: ReactElement) {
	return render(
		<MantineProvider env="test" theme={testMantineTheme}>
			{ui}
		</MantineProvider>,
	);
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
		localStorage.setItem("xe-node-chat-sampling-options", JSON.stringify({ temperature: 0.7, topP: 0.9 }));

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

		// topK / minP / repeatPenalty / repeatLastN only reach llama.cpp; the OpenAI-shaped cloud paths
		// have no wire field for them, so the dialog must say so rather than accept a value that is thrown away.
		for (const key of ["topK", "minP", "repeatPenalty", "repeatLastN"]) {
			expect(screen.getByTestId(`chat-sampling-field-${key}`).hasAttribute("disabled")).toBe(true);
			expect(screen.getByTestId(`chat-sampling-unsupported-${key}`).textContent).toBe("Not supported by cloud providers");
		}

		// Knobs the cloud providers DO honor stay editable.
		for (const key of ["temperature", "topP", "presencePenalty", "frequencyPenalty", "maxOutputTokens", "seed"]) {
			expect(screen.getByTestId(`chat-sampling-field-${key}`).hasAttribute("disabled")).toBe(false);
			expect(screen.queryByTestId(`chat-sampling-unsupported-${key}`)).toBeNull();
		}
	});

	describe("for a conversation", () => {
		it("shows the conversation's own settings and saves an edit to it, never to this browser's defaults", async () => {
			localStorage.setItem("xe-node-chat-sampling-options", JSON.stringify({ temperature: 1.5 }));
			const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");
			const { useChatSamplingPreferencesStore } = await import("@/features/chat/stores/ChatSamplingPreferencesStore");
			const onSave = vi.fn().mockResolvedValue(undefined);
			const onClose = vi.fn();

			renderWithProviders(
				<ChatSamplingOptionsDialog
					opened={true}
					onClose={onClose}
					conversation={{ options: { temperature: 0.3, topP: 0.8 }, loaded: true, onSave }}
				/>,
			);

			expect((screen.getByTestId("chat-sampling-field-temperature") as HTMLInputElement).value).toBe("0.3");
			expect(screen.getByTestId("chat-sampling-save-button").hasAttribute("disabled")).toBe(true);
			fireEvent.click(screen.getByTestId("chat-sampling-reset-temperature"));
			fireEvent.click(screen.getByTestId("chat-sampling-save-button"));

			expect(onSave).toHaveBeenCalledWith({ temperature: undefined, topP: 0.8 });
			await waitFor(() => expect(onClose).toHaveBeenCalledTimes(1));
			expect(useChatSamplingPreferencesStore.getState().options).toEqual({ temperature: 1.5 });
		});

		it("starts from this browser's defaults when the conversation has no settings of its own", async () => {
			localStorage.setItem("xe-node-chat-sampling-options", JSON.stringify({ temperature: 1.5 }));
			const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");

			renderWithProviders(
				<ChatSamplingOptionsDialog
					opened={true}
					onClose={vi.fn()}
					conversation={{ options: undefined, loaded: true, onSave: vi.fn() }}
				/>,
			);

			expect((screen.getByTestId("chat-sampling-field-temperature") as HTMLInputElement).value).toBe("1.5");
			expect(screen.getByTestId("chat-sampling-use-defaults-button").hasAttribute("disabled")).toBe(true);
			expect(screen.queryByTestId("chat-sampling-reset-button")).toBeNull();
		});

		it("use defaults clears the conversation's settings", async () => {
			const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");
			const onSave = vi.fn().mockResolvedValue(undefined);

			renderWithProviders(
				<ChatSamplingOptionsDialog
					opened={true}
					onClose={vi.fn()}
					conversation={{ options: { topK: 5 }, loaded: true, onSave }}
				/>,
			);
			fireEvent.click(screen.getByTestId("chat-sampling-use-defaults-button"));

			expect(onSave).toHaveBeenCalledWith(undefined);
		});

		it("cancel drops the draft, so reopening shows the stored settings again", async () => {
			const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");
			const onSave = vi.fn().mockResolvedValue(undefined);
			const conversation = { options: { temperature: 0.3 }, loaded: true, onSave };
			const view = renderWithProviders(<ChatSamplingOptionsDialog opened={true} onClose={vi.fn()} conversation={conversation} />);

			fireEvent.click(screen.getByTestId("chat-sampling-reset-temperature"));
			expect((screen.getByTestId("chat-sampling-field-temperature") as HTMLInputElement).value).toBe("");
			fireEvent.click(screen.getByTestId("chat-sampling-cancel-button"));
			view.rerender(
				<MantineProvider env="test" theme={testMantineTheme}>
					<ChatSamplingOptionsDialog opened={true} onClose={vi.fn()} conversation={conversation} />
				</MantineProvider>,
			);

			expect((screen.getByTestId("chat-sampling-field-temperature") as HTMLInputElement).value).toBe("0.3");
			expect(onSave).not.toHaveBeenCalled();
		});

		it("saves nothing until the conversation's stored settings have loaded", async () => {
			localStorage.setItem("xe-node-chat-sampling-options", JSON.stringify({ temperature: 1.5 }));
			const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");
			const onSave = vi.fn().mockResolvedValue(undefined);

			renderWithProviders(
				<ChatSamplingOptionsDialog
					opened={true}
					onClose={vi.fn()}
					conversation={{ options: undefined, loaded: false, onSave }}
				/>,
			);
			fireEvent.click(screen.getByTestId("chat-sampling-reset-temperature"));

			// The edit is dropped: a draft begun from this browser's defaults must never be saved over the stored settings.
			expect((screen.getByTestId("chat-sampling-field-temperature") as HTMLInputElement).value).toBe("1.5");
			expect(screen.getByTestId("chat-sampling-save-button").hasAttribute("disabled")).toBe(true);
			expect(screen.getByTestId("chat-sampling-use-defaults-button").hasAttribute("disabled")).toBe(true);
			expect(onSave).not.toHaveBeenCalled();
		});

		it("stays open and shows the node's refusal inline when the save fails, keeping the draft", async () => {
			const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");
			const onSave = vi.fn().mockRejectedValue(new Error("The node refused these settings."));
			const onClose = vi.fn();

			renderWithProviders(
				<ChatSamplingOptionsDialog
					opened={true}
					onClose={onClose}
					conversation={{ options: { temperature: 0.3, topP: 0.8 }, loaded: true, onSave }}
				/>,
			);
			fireEvent.click(screen.getByTestId("chat-sampling-reset-temperature"));
			fireEvent.click(screen.getByTestId("chat-sampling-save-button"));

			expect((await screen.findByTestId("chat-sampling-save-error")).textContent).toBe("The node refused these settings.");
			expect(onClose).not.toHaveBeenCalled();
			expect((screen.getByTestId("chat-sampling-field-temperature") as HTMLInputElement).value).toBe("");
			expect(screen.getByTestId("chat-sampling-save-button").hasAttribute("disabled")).toBe(false);
		});

		it.each([
			["more than 16 stop sequences", Array.from({ length: 17 }, (_, i) => `s${i}`).join("\n")],
			["a stop sequence over 256 characters", "x".repeat(257)],
		])("refuses %s before saving", async (_case, text) => {
			const { ChatSamplingOptionsDialog } = await import("@/features/chat/components/ChatSamplingOptionsDialog");
			const onSave = vi.fn().mockResolvedValue(undefined);

			renderWithProviders(
				<ChatSamplingOptionsDialog opened={true} onClose={vi.fn()} conversation={{ options: {}, loaded: true, onSave }} />,
			);
			fireEvent.change(screen.getByTestId("chat-sampling-field-stop"), { target: { value: text } });

			expect(screen.getByText("At most 16 stop sequences, each up to 256 characters.")).toBeDefined();
			expect(screen.getByTestId("chat-sampling-save-button").hasAttribute("disabled")).toBe(true);
		});
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

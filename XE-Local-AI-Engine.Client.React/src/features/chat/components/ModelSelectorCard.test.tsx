// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import type { ReactElement } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ModelSelectorCard } from "@/features/chat/components/ModelSelectorCard";
import type { ModelOption } from "@/features/chat/models/ChatModels";
import { localDefaultModelValue } from "@/features/chat/models/NodeChatModelSelection";

function renderWithProviders(ui: ReactElement) {
	return render(<MantineProvider>{ui}</MantineProvider>);
}

function localDefaultOption(): ModelOption {
	return { value: localDefaultModelValue, label: "Local default", displayName: "Local runtime default", isReasoningModel: false, isAvailable: true };
}

function chatOption(value: string): ModelOption {
	return { value, label: value, isReasoningModel: false, isAvailable: true };
}

describe("ModelSelectorCard", () => {
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
		Element.prototype.scrollIntoView = vi.fn();
	});

	afterEach(() => {
		cleanup();
	});

	it("surfaces the no-chat-models hint when only the local default is offered (all chat models filtered out)", async () => {
		// The strict picker filter (decision D3) drops embedding/unknown models, so a node with none leaves only the
		// local-default option. The dropdown must explain that, not just show a bare list.
		renderWithProviders(
			<ModelSelectorCard modelOptions={[localDefaultOption()]} selectedModel={localDefaultModelValue} onModelChange={vi.fn()} />,
		);

		fireEvent.click(screen.getByTestId("chat-model-selector-trigger"));

		const hint = await screen.findByTestId("chat-model-selector-no-chat-models");
		expect(hint.textContent).toContain("No chat-capable models");
	});

	it("does not show the no-chat-models hint when a chat model is available", async () => {
		renderWithProviders(
			<ModelSelectorCard
				modelOptions={[localDefaultOption(), chatOption("llama3:8b")]}
				selectedModel={localDefaultModelValue}
				onModelChange={vi.fn()}
			/>,
		);

		fireEvent.click(screen.getByTestId("chat-model-selector-trigger"));

		// Wait for the dropdown to actually render (the chat option appears) so the absence assertion is meaningful.
		await screen.findByTestId("chat-model-selector-option-llama3:8b");
		expect(screen.queryByTestId("chat-model-selector-no-chat-models")).toBeNull();
	});
});

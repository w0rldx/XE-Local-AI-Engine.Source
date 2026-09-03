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
		// The strict picker filter drops embedding and unknown-kind models, so a node with none leaves only the
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


	it("does not render the cloud group when cloudModelOptions is absent", async () => {
		renderWithProviders(
			<ModelSelectorCard modelOptions={[localDefaultOption()]} selectedModel={localDefaultModelValue} onModelChange={vi.fn()} />,
		);

		fireEvent.click(screen.getByTestId("chat-model-selector-trigger"));

		await screen.findByTestId("chat-model-selector-no-chat-models");
		expect(screen.queryByTestId("chat-model-selector-option-codex-mini-latest")).toBeNull();
	});

	it("does not render the cloud group when cloudModelOptions is an empty array", async () => {
		renderWithProviders(
			<ModelSelectorCard
				modelOptions={[localDefaultOption()]}
				cloudModelOptions={[]}
				selectedModel={localDefaultModelValue}
				onModelChange={vi.fn()}
			/>,
		);

		fireEvent.click(screen.getByTestId("chat-model-selector-trigger"));

		await screen.findByTestId("chat-model-selector-no-chat-models");
		expect(screen.queryByTestId("chat-model-selector-option-codex-mini-latest")).toBeNull();
	});

	it("renders the cloud group with options and egress cue when cloudModelOptions is provided", async () => {
		const cloudOption: ModelOption = {
			value: "codex-mini-latest",
			label: "Codex Mini",
			displayName: "Codex Mini",
			isAvailable: true,
			isCloud: true,
		};

		renderWithProviders(
			<ModelSelectorCard
				modelOptions={[localDefaultOption()]}
				cloudModelOptions={[cloudOption]}
				selectedModel={localDefaultModelValue}
				onModelChange={vi.fn()}
			/>,
		);

		fireEvent.click(screen.getByTestId("chat-model-selector-trigger"));

		const option = await screen.findByTestId("chat-model-selector-option-codex-mini-latest");
		expect(option).toBeTruthy();

		const egressCue = await screen.findByTestId("chat-model-selector-cloud-egress-codex-mini-latest");
		expect(egressCue.textContent).toContain("Sent to OpenAI");
	});

	it("renders a separate Azure Foundry group with its own egress cue for AzureFoundry-tagged options", async () => {
		const codexOption: ModelOption = {
			value: "codex-mini-latest",
			label: "Codex Mini",
			displayName: "Codex Mini",
			isAvailable: true,
			isCloud: true,
			provider: "CodexOAuth",
		};
		const azureOption: ModelOption = {
			value: "gpt-4o",
			label: "gpt-4o",
			displayName: "gpt-4o",
			isAvailable: true,
			isCloud: true,
			provider: "AzureFoundry",
		};

		renderWithProviders(
			<ModelSelectorCard
				modelOptions={[localDefaultOption()]}
				cloudModelOptions={[codexOption, azureOption]}
				selectedModel={localDefaultModelValue}
				onModelChange={vi.fn()}
			/>,
		);

		fireEvent.click(screen.getByTestId("chat-model-selector-trigger"));

		expect(await screen.findByTestId("chat-model-selector-option-codex-mini-latest")).toBeTruthy();
		expect(await screen.findByTestId("chat-model-selector-option-gpt-4o")).toBeTruthy();

		// The Azure group label (with its count suffix) and its dedicated egress cue are present.
		expect(screen.getByText(/Cloud \(Azure Foundry\)/)).toBeTruthy();
		const azureEgress = await screen.findByTestId("chat-model-selector-cloud-egress-gpt-4o");
		expect(azureEgress.textContent).toContain("Sent to Azure");
	});

	it("calls onModelChange with the cloud model id when a cloud option is selected", async () => {
		const onModelChange = vi.fn();
		const cloudOption: ModelOption = {
			value: "o4-mini",
			label: "o4-mini",
			displayName: "o4-mini",
			isAvailable: true,
			isCloud: true,
		};

		renderWithProviders(
			<ModelSelectorCard
				modelOptions={[localDefaultOption()]}
				cloudModelOptions={[cloudOption]}
				selectedModel={localDefaultModelValue}
				onModelChange={onModelChange}
			/>,
		);

		fireEvent.click(screen.getByTestId("chat-model-selector-trigger"));

		const option = await screen.findByTestId("chat-model-selector-option-o4-mini");
		fireEvent.click(option);

		expect(onModelChange).toHaveBeenCalledWith("o4-mini");
	});
});

// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ModelSelectorCard } from "@/features/chat/components/ModelSelectorCard";
import type { ModelOption } from "@/features/chat/models/ChatModels";
import { localDefaultModelValue } from "@/features/chat/models/NodeChatModelSelection";
// The shared provider stack — used here rather than the sibling suite's hand-rolled Mantine wrapper because these
// section titles and egress cues are INTERPOLATED translations, which need the app's real i18next instance to resolve.
import { renderWithProviders } from "@/test/RenderWithProviders";

function localDefaultOption(): ModelOption {
	return { value: localDefaultModelValue, label: "Local default", displayName: "Local runtime default", isAvailable: true };
}

function chatOption(value: string): ModelOption {
	return { value, label: value, isAvailable: true };
}

function externalOption(value: string, connectionId: string, connectionName: string, declaredLocality: string): ModelOption {
	return {
		value,
		label: value,
		displayName: value,
		isAvailable: true,
		provider: "external",
		externalConnectionId: connectionId,
		externalConnectionName: connectionName,
		declaredLocality,
	};
}

function codexOption(value: string): ModelOption {
	return { value, label: value, displayName: value, isAvailable: true, isCloud: true, provider: "CodexOAuth" };
}

describe("ModelSelectorCard — external-provider sections", () => {
	afterEach(() => {
		cleanup();
	});

	it("renders one section per external connection instead of folding them into the Codex group", async () => {
		renderWithProviders(
			<ModelSelectorCard
				modelOptions={[localDefaultOption(), chatOption("llama3:8b")]}
				cloudModelOptions={[
					codexOption("gpt-5.3-codex-spark"),
					externalOption("ext:unsloth-box/qwen3-27b", "unsloth-box", "Unsloth box", "local"),
					externalOption("ext:gateway/gpt-4o", "gateway", "Gateway", "cloud"),
				]}
				selectedModel={localDefaultModelValue}
				onModelChange={vi.fn()}
			/>,
		);

		fireEvent.click(screen.getByTestId("chat-model-selector-trigger"));

		expect(await screen.findByText("External · Unsloth box (1)")).toBeTruthy();
		expect(screen.getByText("External · Gateway (1)")).toBeTruthy();
		// The Codex group counts only the Codex entry — external options are no longer swept into it.
		expect(screen.getByText("Cloud (Codex) (1)")).toBeTruthy();
	});

	it("says a declared-local endpoint stays on the network, and names where a declared-cloud one sends", async () => {
		renderWithProviders(
			<ModelSelectorCard
				modelOptions={[localDefaultOption()]}
				cloudModelOptions={[
					externalOption("ext:unsloth-box/qwen3-27b", "unsloth-box", "Unsloth box", "local"),
					externalOption("ext:gateway/gpt-4o", "gateway", "Gateway", "cloud"),
				]}
				selectedModel={localDefaultModelValue}
				onModelChange={vi.fn()}
			/>,
		);

		fireEvent.click(screen.getByTestId("chat-model-selector-trigger"));

		const localCue = await screen.findByTestId("chat-model-selector-cloud-egress-ext:unsloth-box/qwen3-27b");
		expect(localCue.textContent).toBe("Local network");
		expect(screen.getByTestId("chat-model-selector-cloud-egress-ext:gateway/gpt-4o").textContent).toBe("Sent to Gateway");
	});

	it("selects an external model straight from its section", async () => {
		const onModelChange = vi.fn();
		renderWithProviders(
			<ModelSelectorCard
				modelOptions={[localDefaultOption()]}
				cloudModelOptions={[externalOption("ext:unsloth-box/qwen3-27b", "unsloth-box", "Unsloth box", "local")]}
				selectedModel={localDefaultModelValue}
				onModelChange={onModelChange}
			/>,
		);

		fireEvent.click(screen.getByTestId("chat-model-selector-trigger"));
		fireEvent.click(await screen.findByTestId("chat-model-selector-option-ext:unsloth-box/qwen3-27b"));

		expect(onModelChange).toHaveBeenCalledWith("ext:unsloth-box/qwen3-27b");
	});
});

// @vitest-environment jsdom

import { act, cleanup, fireEvent, screen } from "@testing-library/react";
import i18next from "i18next";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import type { ModelOption } from "@/core/models/ModelOption";
import { ChatInputArea } from "@/features/chat/components/ChatInputArea";
import type { ReasoningEffort } from "@/features/chat/models/ChatModels";
import { useChatSamplingPreferencesStore } from "@/features/chat/stores/ChatSamplingPreferencesStore";
import de from "@/locales/de.json";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

const graded: ReasoningEffort[] = ["none", "low", "medium", "high"];
const enforceable: ModelOption = {
	value: "qwen",
	label: "qwen",
	provider: "llamacpp",
	isReasoningModel: true,
	isReasoningControllable: true,
};

// Renders the composer and opens its reasoning menu.
async function openMenu(overrides: Partial<Parameters<typeof ChatInputArea>[0]> = {}): Promise<void> {
	renderWithMantine(
		<ChatInputArea
			availableReasoningEfforts={graded}
			isSending={false}
			modelOptions={[enforceable]}
			selectedModel="qwen"
			reasoningEffort="low"
			onCancel={vi.fn()}
			onModelChange={vi.fn()}
			onReasoningEffortChange={vi.fn()}
			onSend={vi.fn()}
			{...overrides}
		/>,
	);
	fireEvent.click(screen.getByTestId("chat-reasoning-effort-menu-trigger"));
	await screen.findByTestId("chat-reasoning-effort-option-none");
}

function budgetText(effort: ReasoningEffort): string | null {
	return screen.queryByTestId(`chat-reasoning-budget-${effort}`)?.textContent ?? null;
}

describe("ChatInputArea reasoning budget labels", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
		useDeveloperModeStore.getState().actions.setDeveloperMode(false);
		useChatSamplingPreferencesStore.getState().actions.reset();
	});

	afterEach(async () => {
		cleanup();
		await act(() => i18next.changeLanguage("en"));
	});

	it("shows the shipped ladder as exact upper bounds on the graded levels of an enforceable model", async () => {
		await openMenu();

		expect(budgetText("low")).toBe("up to 2,048 tokens");
		expect(budgetText("medium")).toBe("up to 8,192 tokens");
		expect(budgetText("high")).toBe("up to 24,576 tokens");
		expect(budgetText("none")).toBeNull();
	});

	it("formats the bound in German", async () => {
		i18next.addResourceBundle("de", "translation", de, true, true);
		await act(() => i18next.changeLanguage("de"));
		await openMenu();

		expect(budgetText("low")).toBe("bis zu 2.048 Tokens");
	});

	it("shows the node's stored ladder entry and falls back to the default for an unset one", async () => {
		await openMenu({ reasoningBudgetSettings: { reasoningBudgetLowTokens: 3000, reasoningBudgetMediumTokens: null } });

		expect(budgetText("low")).toBe("up to 3,000 tokens");
		expect(budgetText("medium")).toBe("up to 8,192 tokens");
	});

	it("shows the conversation's pin on every graded level", async () => {
		await openMenu({
			conversationSampling: { options: { reasoningBudgetTokens: 2000 }, loaded: true, onSave: vi.fn() },
		});

		for (const effort of ["low", "medium", "high"] as const) {
			expect(budgetText(effort)).toBe("up to 2,000 tokens");
		}
	});

	it("shows this browser's pin only in developer mode, as the send path applies it", async () => {
		useChatSamplingPreferencesStore.getState().actions.setField("reasoningBudgetTokens", 512);
		await openMenu();
		expect(budgetText("high")).toBe("up to 24,576 tokens");
		cleanup();

		useDeveloperModeStore.getState().actions.setDeveloperMode(true);
		await openMenu();
		expect(budgetText("high")).toBe("up to 512 tokens");
	});

	it("shows nothing for a model that does not enforce a budget, nor on auto or binary levels", async () => {
		await openMenu({ modelOptions: [{ ...enforceable, isReasoningControllable: false }] });
		expect(budgetText("low")).toBeNull();
		cleanup();

		await openMenu({ availableReasoningEfforts: ["none", "auto", "on"], reasoningEffort: "auto" });
		expect(budgetText("auto")).toBeNull();
		expect(budgetText("on")).toBeNull();
	});

	it("shows nothing for a cloud model", async () => {
		await openMenu({
			modelOptions: [],
			cloudModelOptions: [{ value: "gpt", label: "gpt", isCloud: true }],
			selectedModel: "gpt",
			availableReasoningEfforts: ["none", "minimal", "low", "medium", "high", "xhigh"],
		});

		expect(budgetText("low")).toBeNull();
		expect(budgetText("minimal")).toBeNull();
	});
});

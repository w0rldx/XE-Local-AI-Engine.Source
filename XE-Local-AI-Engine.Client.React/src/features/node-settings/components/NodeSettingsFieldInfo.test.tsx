// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
	NodeSettingsFieldsCard,
	type NodeSettingsFieldsCardProps,
} from "@/features/node-settings/components/NodeSettingsFieldsCard";
import { toNodeSettingsFieldBounds } from "@/features/node-settings/models/NodeSettingsFieldBounds";
import { toNodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";
import { type NodeSettingsSectionId, nodeSettingsSectionIds } from "@/features/node-settings/models/NodeSettingsSections";
import { RestartRequiredFieldsContext } from "@/features/node-settings/models/RestartRequiredFieldsContext";
import de from "@/locales/de.json";
import en from "@/locales/en.json";
import { installJsdomEnvironmentMocks, testMantineTheme } from "@/test/MantineTestRender";

// The inference and runtime tunables that carry a help card, by wire name. Two are labeled under another i18n key.
const infoFields = [
	"defaultContextTokens",
	"kvCacheType",
	"llamaChatCacheRamMiB",
	"chatCacheReuse",
	"llamaCpuThreadReserve",
	"llamaGpuReservePercent",
	"llamaRamReservePercent",
	"llamaMaxLoadedProcesses",
	"llamaIdleTimeToLiveSeconds",
	"modelFitSafetyMarginPercent",
	"speculativeMode",
	"speculativeDraftModelName",
	"speculativeDraftGpuLayers",
	"speculativeDraftMaxTokens",
	"reasoningBudgetMinimalTokens",
	"reasoningBudgetLowTokens",
	"reasoningBudgetMediumTokens",
	"reasoningBudgetHighTokens",
	"defaultReasoningEffort",
	"chatOutputCapMode",
	"chatOutputCapMaxTokens",
	"compactionAutoEnabled",
	"compactionAutoCompactPercent",
	"compactionRecentMessagesVerbatim",
	"compactionDistillEnabled",
	"contextBudgetRecentTurnKeepCount",
	"providerBudgetMaxCumulativeInputTokens",
	"providerBudgetRecentMessagesToKeep",
	"providerRetryEnabled",
	"providerMaxRetries",
	"knowledgeChatTopK",
	"knowledgeSearchDefaultResults",
	"knowledgeAdaptiveRerankingEnabled",
	"knowledgeRetrievalLatencyBudgetMs",
	"imageMaxLoadedProcesses",
	"imageIdleTimeToLiveSeconds",
	"imageTextEncoderOnGpu",
	"modelLabWarmIdleSeconds",
	"toolPipelineMaxIterationsPerRequest",
	"toolPipelineMaxToolResultChars",
] as const;
const keyAliases: Partial<Record<(typeof infoFields)[number], string>> = {
	llamaChatCacheRamMiB: "llamaChatCacheRam",
	speculativeDraftModelName: "speculativeDraftModel",
};
const fieldTexts = en.pages.nodeSettings.fields as Record<string, { description?: string; info?: string; label?: string }>;
const germanFieldTexts = de.pages.nodeSettings.fields as Record<string, { info?: string }>;

// A form that reveals every conditional field: an external draft model shows the draft fields, custom shows the size.
const revealingForm = {
	...toNodeSettingsFieldsForm(undefined),
	speculativeMode: "draft-simple",
	llamaChatCacheRamMode: "custom" as const,
};

function renderSection(section: NodeSettingsSectionId, onChange = vi.fn()) {
	return render(
		<MantineProvider env="test" theme={testMantineTheme}>
			<RestartRequiredFieldsContext.Provider value={new Set(["chatCacheReuse", "llamaChatCacheRamMiB"])}>
				<NodeSettingsFieldsCard
					section={section}
					form={revealingForm}
					bounds={toNodeSettingsFieldBounds(undefined)}
					errors={{}}
					onChange={onChange as unknown as NodeSettingsFieldsCardProps["onChange"]}
					onApplyPreset={vi.fn()}
					showDeveloperFields={false}
					draftModelOptions={[]}
					intentRouterOptions={[]}
					keepWarmModelOptions={[]}
					autoEffortFastModelOptions={[]}
					backgroundModelOptions={[]}
					rerankerModelOptions={[]}
					embeddingModelOptions={[]}
					onDownloadRecommendedReranker={vi.fn()}
					isDownloadRecommendedRerankerPending={false}
					isRecommendedRerankerInFlight={false}
					onDownloadRecommendedEmbedding={vi.fn()}
					isDownloadRecommendedEmbeddingPending={false}
					isRecommendedEmbeddingInFlight={false}
				/>
			</RestartRequiredFieldsContext.Provider>
		</MantineProvider>,
	);
}

describe("node settings help cards", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("holds an English and a German text for every tunable", () => {
		for (const field of infoFields) {
			const key = keyAliases[field] ?? field;
			expect(fieldTexts[key]?.info, `en ${key}`).toMatch(/\S/);
			expect(germanFieldTexts[key]?.info, `de ${key}`).toMatch(/\S/);
		}
	});

	it("shows the (i) for every tunable somewhere on the page", () => {
		const shown = new Set<string>();
		for (const section of nodeSettingsSectionIds) {
			const { unmount } = renderSection(section);
			for (const button of screen.queryAllByTestId(/^node-settings-info-/)) {
				shown.add(button.getAttribute("data-testid")?.replace("node-settings-info-", "") ?? "");
			}
			unmount();
		}

		expect(infoFields.filter((field) => !shown.has(field))).toEqual([]);
	});

	it("finds the text of a field labeled under another key and keeps the restart badge in the input's name", async () => {
		renderSection("runtime");

		const label = fieldTexts["llamaChatCacheRam"]?.label ?? "";
		// The mode row and the custom size both explain the same setting.
		const buttons = screen.getAllByRole("button", { name: `About ${label}` });
		expect(buttons).toHaveLength(2);
		fireEvent.mouseEnter(buttons[0] as HTMLElement);
		const card = await screen.findByRole("tooltip");
		expect(card.textContent).toContain(fieldTexts["llamaChatCacheRam"]?.info);
		expect(screen.getByRole("radiogroup", { name: `${label}Needs restart` })).toBeDefined();
		const reuse = fieldTexts["chatCacheReuse"]?.label ?? "";
		expect(screen.getByRole("textbox", { name: `${reuse}Needs restart` })).toBeDefined();
	});

	it("shows a switch's card on hover, and a click on the icon does not toggle the switch", async () => {
		const onChange = vi.fn();
		renderSection("chat", onChange);

		const texts = fieldTexts["compactionAutoEnabled"];
		const label = texts?.label ?? "";
		// A Mantine Switch's label wraps its description too; the name stays exactly that, without the (i).
		const toggle = screen.getByRole("switch", { name: `${label} ${texts?.description ?? ""}` });
		const wasChecked = (toggle as HTMLInputElement).checked;
		const info = screen.getByTestId("node-settings-info-compactionAutoEnabled");
		fireEvent.mouseEnter(info);
		const card = await screen.findByRole("tooltip");
		fireEvent.click(info);

		expect(card.textContent).toContain(fieldTexts["compactionAutoEnabled"]?.info);
		expect((toggle as HTMLInputElement).checked).toBe(wasChecked);
		expect(onChange).not.toHaveBeenCalled();
	});
});

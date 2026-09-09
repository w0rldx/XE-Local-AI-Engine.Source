// @vitest-environment jsdom

// The card is presentational: the page owns the probe that asks whether the optional Ollama runtime is configured at
// all (XE_OLLAMA_RUNTIME_ENABLED) and hands the answer down as `ollamaRuntimeDisabled`. The rule these tests hold is
// that the flag, and only the flag, decides between the endpoint field and the disabled line. The fail-open reading of
// a still-loading or failed probe lives with the caller that owns it (`NodeSettings.test.tsx`).

import { describe, expect, it } from "vitest";

import { NodeSettingsRuntimeCard } from "@/features/node-settings/components/NodeSettingsRuntimeCard";
import { toNodeSettingsFieldBounds, toNodeSettingsFieldsForm } from "@/features/node-settings/models/NodeSettingsFieldsModel";
import { renderWithProviders } from "@/test/RenderWithProviders";

function renderCard(ollamaRuntimeDisabled: boolean) {
	const form = { ...toNodeSettingsFieldsForm(undefined), ollamaEndpoint: "http://127.0.0.1:11434" };
	return renderWithProviders(
		<NodeSettingsRuntimeCard
			form={form}
			bounds={toNodeSettingsFieldBounds(undefined)}
			errors={{}}
			onChange={() => undefined}
			draftModelOptions={[]}
			keepWarmModelOptions={[]}
			autoEffortFastModelOptions={[]}
			ollamaRuntimeDisabled={ollamaRuntimeDisabled}
		/>,
	);
}

describe("NodeSettingsRuntimeCard — Ollama endpoint field", () => {
	it("replaces the endpoint field with the disabled line when the runtime is gated off", () => {
		const { getByTestId, queryByTestId } = renderCard(true);

		expect(queryByTestId("node-settings-ollama-endpoint")).toBeNull();
		// The card says why rather than silently losing a field.
		expect(getByTestId("node-settings-ollama-disabled").textContent).toContain("Ollama runtime is disabled on this node.");
	});

	it("renders the endpoint field when the runtime is not gated off", () => {
		const { getByTestId, queryByTestId } = renderCard(false);

		expect(getByTestId("node-settings-ollama-endpoint")).toBeTruthy();
		expect(queryByTestId("node-settings-ollama-disabled")).toBeNull();
	});
});

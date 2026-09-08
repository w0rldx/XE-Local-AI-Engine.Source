// @vitest-environment jsdom

// The card asks the node whether the optional Ollama runtime is configured at all (XE_OLLAMA_RUNTIME_ENABLED) and
// drops the endpoint field when it is not — nothing reads that URL with the gate off. The rule the tests hold is
// FAIL OPEN: only a definite `false` hides the field, so neither a slow probe nor a failing one can take a setting
// away from an operator who still needs it.

import { waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { NodeSettingsRuntimeCard } from "@/features/node-settings/components/NodeSettingsRuntimeCard";
import {
	toNodeSettingsFieldBounds,
	toNodeSettingsFieldsForm,
} from "@/features/node-settings/models/NodeSettingsFieldsModel";
import { jsonRoute, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

// Duplicated deliberately rather than exported from the hook: knip rejects an export whose only consumer is a test,
// and the key is the one thing a test needs to know the probe has actually settled.
const configuredQueryKey = ["ollama-runtime", "configured"];

const runningModelsPath = "models/running";

function snapshot(ollamaConfigured: boolean) {
	return { isAvailable: true, ollamaConfigured, error: null, items: [] };
}

function renderCard() {
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
		/>,
	);
}

describe("NodeSettingsRuntimeCard — Ollama endpoint field", () => {
	it("keeps the field until the probe answers, then replaces it with the disabled line", async () => {
		server.use(jsonRoute("get", runningModelsPath, snapshot(false)));

		const { queryClient, getByTestId, queryByTestId } = renderCard();

		// First paint: the probe has not answered, so the field is still there. This IS the loading case.
		expect(getByTestId("node-settings-ollama-endpoint")).toBeTruthy();
		expect(queryByTestId("node-settings-ollama-disabled")).toBeNull();

		await waitFor(() => expect(queryClient.getQueryData(configuredQueryKey)).toBe(false));

		expect(queryByTestId("node-settings-ollama-endpoint")).toBeNull();
		// The card says why rather than silently losing a field.
		expect(getByTestId("node-settings-ollama-disabled").textContent).toContain("Ollama runtime is disabled on this node.");
	});

	it("keeps the field once the node reports the runtime configured", async () => {
		server.use(jsonRoute("get", runningModelsPath, snapshot(true)));

		const { queryClient, getByTestId, queryByTestId } = renderCard();

		await waitFor(() => expect(queryClient.getQueryData(configuredQueryKey)).toBe(true));

		expect(getByTestId("node-settings-ollama-endpoint")).toBeTruthy();
		expect(queryByTestId("node-settings-ollama-disabled")).toBeNull();
	});

	it("keeps the field when the probe fails, rather than hiding a setting on a transport error", async () => {
		server.use(problemDetailsRoute("get", runningModelsPath, 500, { detail: "Runtime probe failed." }));

		const { queryClient, getByTestId, queryByTestId } = renderCard();

		await waitFor(() => expect(queryClient.getQueryState(configuredQueryKey)?.status).toBe("error"));

		expect(getByTestId("node-settings-ollama-endpoint")).toBeTruthy();
		expect(queryByTestId("node-settings-ollama-disabled")).toBeNull();
	});
});

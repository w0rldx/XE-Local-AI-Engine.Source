// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

// The models list carries the cloud entries (the cloud picker options derive from it), and the details route answers
// per name. No local model is installed, so only the cloud bypass of the installed-list gate can start the fetch.
const { detailsByName, fetchedDetails } = vi.hoisted(() => ({
	detailsByName: new Map<string, { maxContextTokens: number | null; effectiveContextTokens: number | null }>(),
	fetchedDetails: [] as string[],
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	listLocalModelsOptions: () => ({
		queryKey: ["listLocalModels"],
		queryFn: () =>
			Promise.resolve({
				isAvailable: true,
				items: [
					{ modelName: "gpt-5.6-sol", provider: "CodexOAuth", isReasoningCapable: true, isToolCapable: true },
					{ modelName: "my-azure-deployment", provider: "AzureFoundry", isReasoningCapable: false, isToolCapable: true },
				],
			}),
	}),
	getLocalModelDetailsOptions: (options: { path: { modelName: string } }) => ({
		queryKey: ["getLocalModelDetails", options.path.modelName],
		queryFn: () => {
			fetchedDetails.push(options.path.modelName);
			const details = detailsByName.get(options.path.modelName);
			return details === undefined ? Promise.reject(new Error("404")) : Promise.resolve(details);
		},
	}),
}));

import { localDefaultModelValue } from "@/core/models/NodeChatModelSelection";
import { useChatModelSelection } from "@/features/chat/hooks/useChatModelSelection";
import { useNodeChatPreferencesStore } from "@/features/chat/stores/NodeChatPreferencesStore";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

function renderSelection(selectedModel: string) {
	useNodeChatPreferencesStore.getState().actions.setSelectedModel(selectedModel);
	const { wrapper } = createProvidersWrapper();
	return renderHook(() => useChatModelSelection({ isScoped: false, scopedModelPending: false }), { wrapper });
}

describe("useChatModelSelection context capacity for cloud selections", () => {
	afterEach(() => {
		detailsByName.clear();
		fetchedDetails.length = 0;
		useNodeChatPreferencesStore.getState().actions.setSelectedModel(localDefaultModelValue);
	});

	it("takes a Codex selection's declared window from its details", async () => {
		detailsByName.set("gpt-5.6-sol", { maxContextTokens: 272000, effectiveContextTokens: 272000 });

		const { result } = renderSelection("gpt-5.6-sol");

		await waitFor(() => expect(result.current.effectiveMaxContextTokens).toBe(272000));
	});

	it("leaves the capacity unknown for an Azure deployment whose window is not declared", async () => {
		detailsByName.set("my-azure-deployment", { maxContextTokens: null, effectiveContextTokens: null });

		const { result } = renderSelection("my-azure-deployment");

		// Waiting on the fetch itself keeps "unknown" from passing merely because details were never requested.
		await waitFor(() => expect(fetchedDetails).toContain("my-azure-deployment"));
		expect(result.current.selectedModel).toBe("my-azure-deployment");
		expect(result.current.effectiveMaxContextTokens).toBeUndefined();
	});
});

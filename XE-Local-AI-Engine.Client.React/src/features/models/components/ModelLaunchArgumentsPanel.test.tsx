// @vitest-environment jsdom

import { QueryClient } from "@tanstack/react-query";
import { act, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { getModelLaunchArgumentsQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { ModelLaunchArgumentsPanel } from "@/features/models/components/ModelLaunchArgumentsPanel";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

// The override is seeded straight into the cache and never goes stale, so the panel never fetches: each
// `setQueryData` below stands in for a background refetch that returned a new server value. The query observer
// notifies on a later tick, so a test waits for a visible effect of the new server value before asserting.
function createSeededClient(): QueryClient {
	return new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Number.POSITIVE_INFINITY } } });
}

function serve(queryClient: QueryClient, modelName: string, rawArguments: string): void {
	act(() => {
		queryClient.setQueryData(getModelLaunchArgumentsQueryKey({ path: { modelName } }), { modelName, rawArguments });
	});
}

function input(): HTMLTextAreaElement {
	return screen.getByTestId("model-launch-args-input") as HTMLTextAreaElement;
}

function resetButton(): HTMLButtonElement {
	return screen.getByTestId("model-launch-args-reset") as HTMLButtonElement;
}

describe("ModelLaunchArgumentsPanel draft", () => {
	it("keeps an unsaved draft when a refetch changes the server value", async () => {
		const queryClient = createSeededClient();
		serve(queryClient, "alpha", "");
		render(<ModelLaunchArgumentsPanel modelName="alpha" />, createProvidersWrapper({ queryClient }));
		expect(resetButton().disabled).toBe(true);

		fireEvent.change(input(), { target: { value: "--top-k 10" } });
		serve(queryClient, "alpha", "--top-k 50");

		// Reset enables only once the panel has rendered the new, non-empty server value.
		await waitFor(() => expect(resetButton().disabled).toBe(false));
		expect(input().value).toBe("--top-k 10");
	});

	it("follows the server value while the editor is pristine", async () => {
		const queryClient = createSeededClient();
		serve(queryClient, "alpha", "--top-k 40");
		render(<ModelLaunchArgumentsPanel modelName="alpha" />, createProvidersWrapper({ queryClient }));
		expect(input().value).toBe("--top-k 40");

		serve(queryClient, "alpha", "--top-k 50");

		await waitFor(() => expect(input().value).toBe("--top-k 50"));
	});

	it("resets a dirty draft when the model changes", () => {
		const queryClient = createSeededClient();
		serve(queryClient, "alpha", "--top-k 40");
		serve(queryClient, "beta", "--repeat-penalty 1.1");
		const { rerender } = render(<ModelLaunchArgumentsPanel modelName="alpha" />, createProvidersWrapper({ queryClient }));
		fireEvent.change(input(), { target: { value: "--top-k 10" } });

		rerender(<ModelLaunchArgumentsPanel modelName="beta" />);

		expect(input().value).toBe("--repeat-penalty 1.1");
	});
});

// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { modelFitQueryKeys } from "@/features/model-fit/queries/ModelFitQueryKeys";

const { apiMock } = vi.hoisted(() => ({
	apiMock: {
		refreshRecommendations: vi.fn(),
	},
}));

vi.mock("@/features/model-fit/api/ModelFitApi", () => ({
	refreshRecommendations: apiMock.refreshRecommendations,
	// Read functions are imported by the module but unused in these mutation tests.
	listApprovedImages: vi.fn(),
	getLatestRecommendations: vi.fn(),
}));

import { useRefreshRecommendations } from "@/features/model-fit/queries/useModelFit";

// Captures the queryKey of every invalidateQueries call so a test can assert which caches a mutation touched.
const invalidatedKeys: unknown[] = [];

function makeWrapper() {
	invalidatedKeys.length = 0;
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false }, mutations: { retry: false } },
	});
	vi.spyOn(queryClient, "invalidateQueries").mockImplementation((filters) => {
		invalidatedKeys.push((filters as { queryKey?: unknown } | undefined)?.queryKey);
		return Promise.resolve();
	});
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	return { Wrapper };
}

describe("useRefreshRecommendations", () => {
	beforeEach(() => {
		apiMock.refreshRecommendations.mockResolvedValue({ scheduledJobId: "job-1" });
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("fires the existing scheduler job and invalidates the latest-recommendations cache", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useRefreshRecommendations(), { wrapper: Wrapper });

		result.current.mutate("job-1");

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(apiMock.refreshRecommendations).toHaveBeenCalledWith("job-1");
		expect(invalidatedKeys).toContainEqual(modelFitQueryKeys.latestRoot());
	});

	it("surfaces a refresh error and does not invalidate", async () => {
		apiMock.refreshRecommendations.mockRejectedValue(new Error("Request failed with status code 400"));
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useRefreshRecommendations(), { wrapper: Wrapper });

		result.current.mutate("bad-job");

		await waitFor(() => expect(result.current.isError).toBe(true));

		expect(invalidatedKeys).not.toContainEqual(modelFitQueryKeys.latestRoot());
	});
});

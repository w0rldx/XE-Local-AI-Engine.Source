// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The refresh hook dispatches the domain `scheduledJobId` to the generated `refreshRecommendationsMutation()`'s
// `mutationFn`, wrapping it in withResponseValidation. Mock the generated TanStack module so the test owns the
// mutationFn and can assert the wire envelope the hook built without hitting the network.
const { mutationMock } = vi.hoisted(() => ({
	mutationMock: { mutationFn: vi.fn() },
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	refreshRecommendationsMutation: vi.fn(() => ({ mutationFn: mutationMock.mutationFn })),
	// Read/mutation options are imported by the module under test but unused in these refresh-mutation tests. The
	// GGUF browse/runtime/token factories moved out with their hooks; only the advisor's reads + download remain.
	getLatestRecommendationsOptions: vi.fn(),
	getHardwareProfileOptions: vi.fn(),
	startGgufDownloadMutation: vi.fn(),
	getModelCatalogInfoOptions: vi.fn(),
	refreshModelCatalogMutation: vi.fn(() => ({ mutationFn: mutationMock.mutationFn })),
}));

import { modelFitInvalidationKey, modelFitQueryIds, useRefreshRecommendations } from "@/features/model-fit/queries/useModelFit";
import { createProvidersWrapper } from "@/test/RenderWithProviders";

// The generated query key the refresh mutation invalidates (partial `_id` match), built via the production helper.
const LATEST_KEY = modelFitInvalidationKey(modelFitQueryIds.latest);

const invalidatedKeys: unknown[] = [];

function makeWrapper() {
	invalidatedKeys.length = 0;
	const { wrapper, queryClient } = createProvidersWrapper();
	vi.spyOn(queryClient, "invalidateQueries").mockImplementation((filters) => {
		invalidatedKeys.push((filters as { queryKey?: unknown } | undefined)?.queryKey);
		return Promise.resolve();
	});
	return { wrapper };
}

describe("useRefreshRecommendations", () => {
	beforeEach(() => {
		mutationMock.mutationFn.mockResolvedValue({ scheduledJobId: "job-1" });
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("dispatches the domain job id to the generated mutation body and invalidates the latest cache", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useRefreshRecommendations(), { wrapper });

		result.current.mutate({ scheduledJobId: "job-1" });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		// TanStack v5 calls mutationFn(variables, context) — assert the variables via the first call arg only
		// (a toHaveBeenCalledWith({...}) would fail on the 2nd context argument). The hook spreads the domain variables
		// into the body, so with no overrides the body carries just the job id.
		expect(mutationMock.mutationFn.mock.calls[0]?.[0]).toEqual({ body: { scheduledJobId: "job-1" } });
		expect(invalidatedKeys).toContainEqual(LATEST_KEY);
	});

	it("forwards a use-case override into the generated mutation body", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useRefreshRecommendations(), { wrapper });

		result.current.mutate({ scheduledJobId: "job-1", useCase: "general" });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationMock.mutationFn.mock.calls[0]?.[0]).toEqual({ body: { scheduledJobId: "job-1", useCase: "general" } });
	});

	it("forwards a breadth limit override into the generated mutation body", async () => {
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useRefreshRecommendations(), { wrapper });

		result.current.mutate({ scheduledJobId: "job-1", useCase: "general", limit: 20 });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(mutationMock.mutationFn.mock.calls[0]?.[0]).toEqual({
			body: { scheduledJobId: "job-1", useCase: "general", limit: 20 },
		});
	});

	it("surfaces a refresh error and does not invalidate", async () => {
		mutationMock.mutationFn.mockRejectedValue(new Error("Request failed with status code 400"));
		const { wrapper } = makeWrapper();
		const { result } = renderHook(() => useRefreshRecommendations(), { wrapper });

		result.current.mutate({ scheduledJobId: "bad-job" });

		await waitFor(() => expect(result.current.isError).toBe(true));

		expect(invalidatedKeys).not.toContainEqual(LATEST_KEY);
	});
});

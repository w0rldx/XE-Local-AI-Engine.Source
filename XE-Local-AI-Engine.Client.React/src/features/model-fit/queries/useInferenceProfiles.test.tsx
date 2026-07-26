// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// The inference-profile mutation hooks dispatch their domain variables to the generated mutationFn's `{ body }`
// envelope (wrapped in withResponseValidation) and invalidate the profiles list on success. Mock the generated
// TanStack module so the test owns each mutationFn and can assert the wire envelope + the invalidation, offline.
const { exploreMock, benchmarkMock, freezeMock, invalidateMock } = vi.hoisted(() => ({
	exploreMock: { mutationFn: vi.fn() },
	benchmarkMock: { mutationFn: vi.fn() },
	freezeMock: { mutationFn: vi.fn() },
	invalidateMock: { mutationFn: vi.fn() },
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	listInferenceProfilesOptions: vi.fn(() => ({ queryKey: [{ _id: "listInferenceProfiles" }], queryFn: vi.fn() })),
	exploreInferenceProfileMutation: vi.fn(() => ({ mutationFn: exploreMock.mutationFn })),
	benchmarkInferenceProfileMutation: vi.fn(() => ({ mutationFn: benchmarkMock.mutationFn })),
	freezeInferenceProfileMutation: vi.fn(() => ({ mutationFn: freezeMock.mutationFn })),
	invalidateInferenceProfileMutation: vi.fn(() => ({ mutationFn: invalidateMock.mutationFn })),
	// Referenced at import time by the co-imported useModelFit module; unused here.
	getLatestRecommendationsOptions: vi.fn(),
	getHardwareProfileOptions: vi.fn(),
	refreshRecommendationsMutation: vi.fn(),
}));

import {
	inferenceProfileQueryIds,
	useBenchmarkInferenceProfile,
	useExploreInferenceProfile,
	useFreezeInferenceProfile,
	useInvalidateInferenceProfile,
} from "@/features/model-fit/queries/useInferenceProfiles";
import { modelFitInvalidationKey } from "@/features/model-fit/queries/useModelFit";

const LIST_KEY = modelFitInvalidationKey(inferenceProfileQueryIds.list);

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

describe("useInferenceProfiles mutations", () => {
	beforeEach(() => {
		exploreMock.mutationFn.mockResolvedValue({ profile: { id: "p1" } });
		freezeMock.mutationFn.mockResolvedValue({ profile: { id: "p1" } });
		invalidateMock.mutationFn.mockResolvedValue({ profile: { id: "p1" } });
		benchmarkMock.mutationFn.mockResolvedValue({
			snapshotId: "snap-1",
			metrics: {
				role: "Chat",
				tokensPerSecond: 42,
				vramAfterBytes: 6_656_000_000,
				globalFreeVramAfterBytes: 6_656_000_000,
				processBudgetVramAfterBytes: 7_200_000_000,
				externalPressureDetected: false,
			},
			profile: { id: "p1", status: "Explored" },
		});
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("explore dispatches { modelName, role } to the generated body and invalidates the list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useExploreInferenceProfile(), { wrapper: Wrapper });

		result.current.mutate({ modelName: "unsloth/Qwen3-4B-GGUF", role: "coding" });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(exploreMock.mutationFn.mock.calls[0]?.[0]).toEqual({ body: { modelName: "unsloth/Qwen3-4B-GGUF", role: "coding" } });
		expect(invalidatedKeys).toContainEqual(LIST_KEY);
	});

	it("benchmark dispatches { profileId }, maps the metrics, and invalidates the list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useBenchmarkInferenceProfile(), { wrapper: Wrapper });

		result.current.mutate({ profileId: "p1" });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(benchmarkMock.mutationFn.mock.calls[0]?.[0]).toEqual({ body: { profileId: "p1" } });
		// The hook maps the wire response to the domain result shape.
		expect(result.current.data).toEqual({
			snapshotId: "snap-1",
			metrics: {
				role: "Chat",
				tokensPerSecond: 42,
				ppTokensPerSecond: null,
				ttftMs: null,
				totalLatencyMs: null,
				cacheHitRate: null,
				toolLoopMs: null,
				itemsPerSecond: null,
				inputTokensPerSecond: null,
				p50LatencyMs: null,
				p95LatencyMs: null,
				batchSize: null,
				outputDimension: null,
				valuesFinite: null,
				deterministicOutput: null,
				vramLoadBytes: null,
				vramAfterBytes: 6_656_000_000,
				globalFreeVramLoadBytes: null,
				globalFreeVramAfterBytes: 6_656_000_000,
				processBudgetVramLoadBytes: null,
				processBudgetVramAfterBytes: 7_200_000_000,
				minimumGlobalFreeVramBytes: null,
				minimumProcessBudgetVramBytes: null,
				peakProcessRamBytes: null,
				externalPressureDetected: false,
				runs: null,
			},
			profile: expect.objectContaining({ id: "p1", status: "explored" }),
		});
		expect(invalidatedKeys).toContainEqual(LIST_KEY);
	});

	it("freeze dispatches { profileId } and invalidates the list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useFreezeInferenceProfile(), { wrapper: Wrapper });

		result.current.mutate({ profileId: "p1" });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(freezeMock.mutationFn.mock.calls[0]?.[0]).toEqual({ body: { profileId: "p1" } });
		expect(invalidatedKeys).toContainEqual(LIST_KEY);
	});

	it("invalidate dispatches { profileId } and invalidates the list", async () => {
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useInvalidateInferenceProfile(), { wrapper: Wrapper });

		result.current.mutate({ profileId: "p1" });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));

		expect(invalidateMock.mutationFn.mock.calls[0]?.[0]).toEqual({ body: { profileId: "p1" } });
		expect(invalidatedKeys).toContainEqual(LIST_KEY);
	});

	it("does not invalidate the list when a mutation fails", async () => {
		freezeMock.mutationFn.mockRejectedValue(new Error("Request failed with status code 400"));
		const { Wrapper } = makeWrapper();
		const { result } = renderHook(() => useFreezeInferenceProfile(), { wrapper: Wrapper });

		result.current.mutate({ profileId: "bad" });

		await waitFor(() => expect(result.current.isError).toBe(true));

		expect(invalidatedKeys).not.toContainEqual(LIST_KEY);
	});
});

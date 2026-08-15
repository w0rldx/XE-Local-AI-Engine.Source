// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getAppUpdateStatusOptions: vi.fn(),
	// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
	getAppUpdateStatusQueryKey: vi.fn(() => [{ _id: "getAppUpdateStatus" }]),
	applyAppUpdateMutation: vi.fn(),
}));

vi.mock("@/core/api/ResponseValidation", () => ({
	withResponseValidation: (opts: unknown) => opts,
	callWithResponseValidation: <T,>(call: Promise<T>) => call,
}));
vi.mock("@/core/api/generated/sdk.gen", () => ({ getAppUpdateStatus: vi.fn() }));

import {
	applyAppUpdateMutation,
	getAppUpdateStatusOptions,
	getAppUpdateStatusQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { getAppUpdateStatus } from "@/core/api/generated/sdk.gen";
import { createProvidersWrapper, createTestQueryClient } from "@/test/RenderWithProviders";
import {
	useApplyAppUpdate,
	useAppUpdateStatus,
	useProbeAppUpdateStatus,
	useRefreshAppUpdateStatus,
} from "./useAppUpdate";

const statusMock = vi.mocked(getAppUpdateStatusOptions);

// Several cases below assert what a mutation SEEDED into the cache (setQueryData) while nothing is observing that
// key. The harness default of `gcTime: 0` collects such an entry the moment it lands, so this file overrides the
// retention — the one place the shared default is the wrong fit, and the reason `queryClient` is an option.
function makeWrapper() {
	const queryClient = createTestQueryClient();
	queryClient.setDefaultOptions({
		queries: { retry: false, gcTime: Number.POSITIVE_INFINITY },
		mutations: { retry: false },
	});
	return createProvidersWrapper({ queryClient });
}

afterEach(() => vi.clearAllMocks());

describe("useAppUpdateStatus", () => {
	it("returns anonymous public update status", async () => {
		statusMock.mockReturnValue({
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
			queryKey: [{ _id: "getAppUpdateStatus" }],
			queryFn: async () => ({ isDesktop: true, isConfigured: true, updateAvailable: true, currentVersion: "1.0.0" }),
		} as never);
		const { wrapper, queryClient } = makeWrapper();

		const { result } = renderHook(() => useAppUpdateStatus(), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(result.current.data?.isConfigured).toBe(true);
		expect(result.current.data?.updateAvailable).toBe(true);
		const query = queryClient.getQueryCache().find({
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
			queryKey: [{ _id: "getAppUpdateStatus" }],
		});
		const observerOptions = query?.options as { refetchInterval?: number } | undefined;
		expect(observerOptions?.refetchInterval).toBe(60_000);
	});
});

describe("useRefreshAppUpdateStatus", () => {
	it("forces refresh:true and seeds the default cache", async () => {
		const refreshedSnapshot = { isDesktop: true, isConfigured: true, updateAvailable: false, currentVersion: "1.0.0" };
		statusMock.mockImplementation((opts) => ({
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
			queryKey: [{ _id: "getAppUpdateStatus", query: opts?.query }],
			queryFn: async () => refreshedSnapshot,
		}) as never);
		vi.mocked(getAppUpdateStatusQueryKey).mockImplementation((opts) => [
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
			{ _id: "getAppUpdateStatus", query: opts?.query },
		] as never);
		const { wrapper, queryClient } = makeWrapper();
		const { result } = renderHook(() => useRefreshAppUpdateStatus(), { wrapper });

		result.current.mutate();

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(statusMock).toHaveBeenCalledWith({ query: { refresh: true } });
		expect(queryClient.getQueryData(getAppUpdateStatusQueryKey({ query: { refresh: null } }))).toEqual(refreshedSnapshot);
	});
});

describe("useProbeAppUpdateStatus", () => {
	it("reads restart identity without replacing the displayed update cache", async () => {
		vi.mocked(getAppUpdateStatus).mockResolvedValue({
			data: {
				currentVersion: "1.0.0",
				availableVersion: null,
				updateAvailable: false,
				isConfigured: true,
				isDesktop: true,
				checkStatus: "ready",
				lastCheckedUtc: 1_700_000_000_000,
			},
		} as never);
		const { wrapper, queryClient } = makeWrapper();
		const key = getAppUpdateStatusQueryKey({ query: { refresh: null } });
		queryClient.setQueryData(key, {
			currentVersion: "1.0.0",
			availableVersion: "1.1.0",
			updateAvailable: true,
			isConfigured: true,
			isDesktop: true,
			checkStatus: "ready",
			lastCheckedUtc: 1_700_000_000_000,
		});
		const { result } = renderHook(() => useProbeAppUpdateStatus(), { wrapper });

		result.current.mutate();

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(result.current.data?.currentVersion).toBe("1.0.0");
		expect(queryClient.getQueryData(key)).toMatchObject({
			availableVersion: "1.1.0",
			updateAvailable: true,
		});
	});
});

describe("useApplyAppUpdate", () => {
	it("clears a stale available update when the live apply reports that nothing was applied", async () => {
		vi.mocked(applyAppUpdateMutation).mockReturnValue({
			mutationFn: async () => ({ applying: false }),
		} as never);
		const { wrapper, queryClient } = makeWrapper();
		const key = getAppUpdateStatusQueryKey({ query: { refresh: null } });
		queryClient.setQueryData(key, {
			currentVersion: "1.0.0",
			availableVersion: "1.1.0",
			updateAvailable: true,
			isConfigured: true,
			isDesktop: true,
			checkStatus: "ready",
			lastCheckedUtc: 1_700_000_000_000,
		});
		const { result } = renderHook(() => useApplyAppUpdate(), { wrapper });

		result.current.mutate({} as never);

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(queryClient.getQueryData(key)).toMatchObject({
			availableVersion: null,
			updateAvailable: false,
			checkStatus: "ready",
		});
	});
});

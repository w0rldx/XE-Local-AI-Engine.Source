// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
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
import {
	useApplyAppUpdate,
	useAppUpdateStatus,
	useProbeAppUpdateStatus,
	useRefreshAppUpdateStatus,
} from "./useAppUpdate";

const statusMock = vi.mocked(getAppUpdateStatusOptions);

function makeWrapper() {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false }, mutations: { retry: false } } });
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	return { Wrapper, queryClient };
}

afterEach(() => vi.clearAllMocks());

describe("useAppUpdateStatus", () => {
	it("returns anonymous public update status", async () => {
		statusMock.mockReturnValue({
			// biome-ignore lint/style/useNamingConvention: generated hey-api query-key discriminator field.
			queryKey: [{ _id: "getAppUpdateStatus" }],
			queryFn: async () => ({ isDesktop: true, isConfigured: true, updateAvailable: true, currentVersion: "1.0.0" }),
		} as never);
		const { Wrapper, queryClient } = makeWrapper();

		const { result } = renderHook(() => useAppUpdateStatus(), { wrapper: Wrapper });

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
		const { Wrapper, queryClient } = makeWrapper();
		const { result } = renderHook(() => useRefreshAppUpdateStatus(), { wrapper: Wrapper });

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
		const { Wrapper, queryClient } = makeWrapper();
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
		const { result } = renderHook(() => useProbeAppUpdateStatus(), { wrapper: Wrapper });

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
		const { Wrapper, queryClient } = makeWrapper();
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
		const { result } = renderHook(() => useApplyAppUpdate(), { wrapper: Wrapper });

		result.current.mutate({} as never);

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(queryClient.getQueryData(key)).toMatchObject({
			availableVersion: null,
			updateAvailable: false,
			checkStatus: "ready",
		});
	});
});

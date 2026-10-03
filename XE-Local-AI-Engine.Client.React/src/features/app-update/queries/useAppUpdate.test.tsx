// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	getAppUpdateStatusOptions: vi.fn(),
	getAppUpdateStatusQueryKey: vi.fn(() => [{ _id: "getAppUpdateStatus" }]),
	applyAppUpdateMutation: vi.fn(),
	setAppUpdateChannelMutation: vi.fn(),
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
	setAppUpdateChannelMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { getAppUpdateStatus } from "@/core/api/generated/sdk.gen";
import { createProvidersWrapper, createTestQueryClient } from "@/test/RenderWithProviders";
import {
	useApplyAppUpdate,
	useAppUpdateStatus,
	useProbeAppUpdateStatus,
	useRefreshAppUpdateStatus,
	useSetAppUpdateChannel,
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
			queryKey: [{ _id: "getAppUpdateStatus" }],
			queryFn: async () => ({ isDesktop: true, isConfigured: true, updateAvailable: true, currentVersion: "1.0.0" }),
		} as never);
		const { wrapper, queryClient } = makeWrapper();

		const { result } = renderHook(() => useAppUpdateStatus(), { wrapper });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(result.current.data?.isConfigured).toBe(true);
		expect(result.current.data?.updateAvailable).toBe(true);
		const query = queryClient.getQueryCache().find({
			queryKey: [{ _id: "getAppUpdateStatus" }],
		});
		const observerOptions = query?.options as { refetchInterval?: number } | undefined;
		expect(observerOptions?.refetchInterval).toBe(60_000);
	});
});

describe("useRefreshAppUpdateStatus", () => {
	it("forces refresh:true and seeds the default cache", async () => {
		const refreshedSnapshot = { isDesktop: true, isConfigured: true, updateAvailable: false, currentVersion: "1.0.0" };
		statusMock.mockImplementation(
			(opts) =>
				({
					queryKey: [{ _id: "getAppUpdateStatus", query: opts?.query }],
					queryFn: async () => refreshedSnapshot,
				}) as never,
		);
		vi.mocked(getAppUpdateStatusQueryKey).mockImplementation(
			(opts) => [{ _id: "getAppUpdateStatus", query: opts?.query }] as never,
		);
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

describe("useSetAppUpdateChannel", () => {
	it("seeds the cached status with the channel endpoint's own response", async () => {
		const fresh = {
			currentVersion: "1.0.0",
			availableVersion: null,
			updateAvailable: false,
			isConfigured: true,
			isDesktop: true,
			checkStatus: "ready",
			lastCheckedUtc: 1_700_000_000_000,
			selectedChannel: "development",
			defaultChannel: "stable",
			availableChannels: ["stable", "preview", "development"],
			recommendedVersion: null,
			availableChannel: null,
		};
		vi.mocked(setAppUpdateChannelMutation).mockReturnValue({ mutationFn: async () => fresh } as never);
		const { wrapper, queryClient } = makeWrapper();
		const key = getAppUpdateStatusQueryKey({ query: { refresh: null } });
		const { result } = renderHook(() => useSetAppUpdateChannel(), { wrapper });

		await result.current.mutateAsync({ body: { channel: "development" } } as never);

		await waitFor(() => expect(queryClient.getQueryData(key)).toMatchObject({ selectedChannel: "development" }));
	});

	it("leaves the cached status alone when the channel change fails", async () => {
		vi.mocked(setAppUpdateChannelMutation).mockReturnValue({
			mutationFn: async () => {
				throw new Error("refused");
			},
		} as never);
		const { wrapper, queryClient } = makeWrapper();
		const key = getAppUpdateStatusQueryKey({ query: { refresh: null } });
		const { result } = renderHook(() => useSetAppUpdateChannel(), { wrapper });

		result.current.mutate({ body: { channel: "development" } } as never);

		await waitFor(() => expect(result.current.isError).toBe(true));
		expect(queryClient.getQueryData(key)).toBeUndefined();
	});
});

describe("channel change racing a refresh", () => {
	const status = (selectedChannel: string) => ({
		currentVersion: "1.0.0",
		availableVersion: null,
		updateAvailable: false,
		isConfigured: true,
		isDesktop: true,
		checkStatus: "ready",
		lastCheckedUtc: 1_700_000_000_000,
		selectedChannel,
		defaultChannel: "stable",
		availableChannels: ["stable", "preview", "development"],
		recommendedVersion: null,
		availableChannel: null,
	});

	// The server's stored channel; the local GET (refresh:null) always answers with it, like GetStatusAsync.
	function mockServer(stored: { channel: string }, refreshResponse: Promise<unknown>) {
		statusMock.mockImplementation(
			(opts) =>
				({
					queryKey: [{ _id: "getAppUpdateStatus", query: opts?.query }],
					queryFn: async () => (opts?.query?.refresh ? refreshResponse : status(stored.channel)),
				}) as never,
		);
		vi.mocked(getAppUpdateStatusQueryKey).mockImplementation(
			(opts) => [{ _id: "getAppUpdateStatus", query: opts?.query }] as never,
		);
	}

	function renderAll() {
		const { wrapper } = makeWrapper();
		return renderHook(
			() => ({ status: useAppUpdateStatus(), refresh: useRefreshAppUpdateStatus(), channel: useSetAppUpdateChannel() }),
			{ wrapper },
		);
	}

	it("keeps the new channel when a refresh started under the old one answers after the change", async () => {
		const stored = { channel: "stable" };
		let answerRefresh: (value: unknown) => void = () => undefined;
		mockServer(stored, new Promise((resolve) => (answerRefresh = resolve)));
		vi.mocked(setAppUpdateChannelMutation).mockReturnValue({
			mutationFn: async () => {
				stored.channel = "development";
				return status("development");
			},
		} as never);
		const { result } = renderAll();
		await waitFor(() => expect(result.current.status.data?.selectedChannel).toBe("stable"));

		result.current.refresh.mutate();
		await result.current.channel.mutateAsync({ body: { channel: "development" } } as never);
		await waitFor(() => expect(result.current.status.data?.selectedChannel).toBe("development"));

		answerRefresh(status("stable"));
		await waitFor(() => expect(result.current.refresh.isSuccess).toBe(true));

		expect(result.current.status.data?.selectedChannel).toBe("development");
		await waitFor(() => expect(result.current.status.isFetching).toBe(false));
		expect(result.current.status.data?.selectedChannel).toBe("development");
	});

	it("reconciles the stored channel when the change's own check fails", async () => {
		const stored = { channel: "stable" };
		mockServer(stored, new Promise(() => undefined));
		vi.mocked(setAppUpdateChannelMutation).mockReturnValue({
			mutationFn: async () => {
				// The server persists first, then its check throws: the node moved even though the PUT failed.
				stored.channel = "development";
				throw new Error("check failed");
			},
		} as never);
		const { result } = renderAll();
		await waitFor(() => expect(result.current.status.data?.selectedChannel).toBe("stable"));

		result.current.channel.mutate({ body: { channel: "development" } } as never);

		await waitFor(() => expect(result.current.channel.isError).toBe(true));
		await waitFor(() => expect(result.current.status.data?.selectedChannel).toBe("development"));
	});
});

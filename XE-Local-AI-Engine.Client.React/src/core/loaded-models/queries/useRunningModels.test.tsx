// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

// useRunningModels wraps the generated TanStack `*Options()`, so its generated module is mocked with a test-owned
// options object.
const { runningModelsGenMock } = vi.hoisted(() => ({
	runningModelsGenMock: {
		listRunningModelsOptions: vi.fn(),
		ejectRunningModelMutation: vi.fn(),
	},
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => runningModelsGenMock);

// The hub's own lifecycle is pinned in useRuntimeResidencyHub.test; here it is a switch the test flips.
const hubState = vi.hoisted(() => ({ isLive: false, calls: [] as [boolean, unknown][] }));
vi.mock("@/core/api/signalr/useRuntimeResidencyHub", () => ({
	useRuntimeResidencyHub: (enabled: boolean, queryKey: unknown) => {
		hubState.calls.push([enabled, queryKey]);
		return { isLive: hubState.isLive };
	},
}));

import {
	runningModelsPollIntervalMs,
	runningModelsPushFloorMs,
	useEjectRunningModel,
	useRunningModels,
} from "@/core/loaded-models/queries/useRunningModels";

function makeClient() {
	return new QueryClient({
		defaultOptions: { queries: { retry: false } },
	});
}

function makeWrapper(queryClient: QueryClient) {
	return function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	};
}

describe("useRunningModels (llama.cpp) polling", () => {
	afterEach(() => {
		vi.useRealTimers();
		vi.clearAllMocks();
		hubState.isLive = false;
		hubState.calls.length = 0;
	});

	it("re-fetches on its own poll interval, since loads/evictions happen without any client mutation to invalidate on", async () => {
		vi.useFakeTimers({ shouldAdvanceTime: true });
		const queryFn = vi.fn().mockResolvedValue({ items: [] });
		runningModelsGenMock.listRunningModelsOptions.mockReturnValue({
			queryKey: [{ _id: "listRunningModels" }],
			queryFn,
		});
		const queryClient = makeClient();

		const { result } = renderHook(() => useRunningModels(), { wrapper: makeWrapper(queryClient) });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		expect(queryFn).toHaveBeenCalledTimes(1);

		// One full poll interval later the list re-fetches on its own — models appear as chat sends warm them and
		// disappear on idle-TTL eviction, so without this the page only ever refreshed on manual reload.
		await vi.advanceTimersByTimeAsync(runningModelsPollIntervalMs + 100);
		await waitFor(() => expect(queryFn.mock.calls.length).toBeGreaterThanOrEqual(2));
	});

	it("subscribes its own key to the residency hub, and not while disabled", () => {
		runningModelsGenMock.listRunningModelsOptions.mockReturnValue({ queryKey: [{ _id: "listRunningModels" }], queryFn: vi.fn() });
		const queryClient = makeClient();

		renderHook(() => useRunningModels(false), { wrapper: makeWrapper(queryClient) });

		expect(hubState.calls).toContainEqual([false, [{ _id: "listRunningModels" }]]);
		expect(hubState.calls.some(([enabled]) => enabled)).toBe(false);
	});

	it("drops to the 60s push floor while the residency hub is live", async () => {
		hubState.isLive = true;
		vi.useFakeTimers({ shouldAdvanceTime: true });
		const queryFn = vi.fn().mockResolvedValue({ items: [] });
		runningModelsGenMock.listRunningModelsOptions.mockReturnValue({
			queryKey: [{ _id: "listRunningModels" }],
			queryFn,
		});
		const queryClient = makeClient();

		const { result } = renderHook(() => useRunningModels(), { wrapper: makeWrapper(queryClient) });

		await waitFor(() => expect(result.current.isSuccess).toBe(true));
		await vi.advanceTimersByTimeAsync(runningModelsPollIntervalMs + 100);
		expect(queryFn).toHaveBeenCalledTimes(1);

		await vi.advanceTimersByTimeAsync(runningModelsPushFloorMs);
		await waitFor(() => expect(queryFn).toHaveBeenCalledTimes(2));
	});

	it("pins the fallback cadence to 4s and the push floor to 60s", () => {
		expect(runningModelsPollIntervalMs).toBe(4000);
		expect(runningModelsPushFloorMs).toBe(60_000);
	});
});

describe("useEjectRunningModel", () => {
	afterEach(() => vi.clearAllMocks());

	it("refreshes the llama.cpp runtime status too, so the Runtimes page stops counting the ejected model", async () => {
		runningModelsGenMock.ejectRunningModelMutation.mockReturnValue({
			mutationFn: vi.fn().mockResolvedValue({ modelName: "m", role: "Chat", outcome: "ejected" }),
		});
		const queryClient = makeClient();
		queryClient.setQueryData([{ _id: "getLlamaCppRuntime" }], { runningProcessCount: 1 });
		queryClient.setQueryData([{ _id: "listRunningModels" }], { items: [] });

		const { result } = renderHook(() => useEjectRunningModel(), { wrapper: makeWrapper(queryClient) });
		await result.current.mutateAsync({ modelName: "m" });

		expect(queryClient.getQueryState([{ _id: "getLlamaCppRuntime" }])?.isInvalidated).toBe(true);
		expect(queryClient.getQueryState([{ _id: "listRunningModels" }])?.isInvalidated).toBe(true);
	});
});

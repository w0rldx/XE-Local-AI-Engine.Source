// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { resetSharedHubConnectionsForTest } from "@/core/api/signalr/SharedHubConnection";
import { IMAGE_JOB_STATUS_CHANGED } from "@/features/images/models/ImageModels";
import { imageJobProgressKey, useImageJobHub } from "@/features/images/hooks/useImageJobHub";

// Captured event handlers registered via connection.on, so a test can drive a server push by name.
const registeredHandlers = new Map<string, (payload: unknown) => void>();
const stopSpy = vi.fn(() => Promise.resolve());
const startSpy = vi.fn(() => Promise.resolve());
const invokeSpy = vi.fn(() => Promise.resolve());
let connectionState = "Disconnected";

// Mock the SignalR client: `on` captures handlers, `start` flips to Connected, `invoke` is spied, and `state`/
// `onreconnected` model the connect lifecycle the subscribe logic guards on (mirrors usePreviewWorkflowHub.test).
vi.mock("@microsoft/signalr", () => {
	class FakeBuilder {
		withUrl() {
			return this;
		}
		withAutomaticReconnect() {
			return this;
		}
		configureLogging() {
			return this;
		}
		build() {
			return {
				get state() {
					return connectionState;
				},
				on: (name: string, handler: (payload: unknown) => void) => registeredHandlers.set(name, handler),
				off: (name: string) => registeredHandlers.delete(name),
				onreconnected: () => undefined,
				onreconnecting: () => undefined,
				onclose: () => undefined,
				start: () => {
					connectionState = "Connected";
					return startSpy();
				},
				stop: stopSpy,
				invoke: invokeSpy,
			};
		}
	}
	return {
		HubConnectionBuilder: FakeBuilder,
		HubConnectionState: { Connected: "Connected", Disconnected: "Disconnected" },
		LogLevel: { Warning: 3 },
	};
});

vi.mock("@/core/auth/stores/NodeAuthStore", () => ({
	useNodeAuthStore: { getState: () => ({ accessToken: "token" }) },
}));

function push(payload: Record<string, unknown>): void {
	registeredHandlers.get(IMAGE_JOB_STATUS_CHANGED)?.(payload);
}

function basePush(jobId: string, seq: number): Record<string, unknown> {
	return { jobId, phase: "Generating", occurredAtUtc: 1, seq };
}

function renderHub(jobIds: readonly string[]) {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	return { ...renderHook(({ ids }) => useImageJobHub(ids), { wrapper: Wrapper, initialProps: { ids: jobIds } }), invalidateSpy, queryClient };
}

describe("useImageJobHub", () => {
	beforeEach(() => {
		resetSharedHubConnectionsForTest();
		registeredHandlers.clear();
		startSpy.mockClear();
		stopSpy.mockClear();
		invokeSpy.mockClear();
		connectionState = "Disconnected";
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("subscribes to each active job once the connection is up", async () => {
		renderHub(["job-1", "job-2"]);
		await waitFor(() => expect(invokeSpy).toHaveBeenCalledWith("Subscribe", "job-1"));
		expect(invokeSpy).toHaveBeenCalledWith("Subscribe", "job-2");
	});

	it("invalidates the jobs cache when the coarse status moves", async () => {
		const { invalidateSpy } = renderHub(["job-1"]);
		await waitFor(() => expect(registeredHandlers.has(IMAGE_JOB_STATUS_CHANGED)).toBe(true));

		push(basePush("job-1", 1));
		expect(invalidateSpy).toHaveBeenCalledTimes(1);
	});

	// A step tick changes nothing the list endpoint serves, and a running job emits one about every second. Refetching
	// the whole job list on each would be pure churn — the live detail is already in the push.
	it("does not refetch the job list for a step tick within the same status", async () => {
		const { invalidateSpy } = renderHub(["job-1"]);
		await waitFor(() => expect(registeredHandlers.has(IMAGE_JOB_STATUS_CHANGED)).toBe(true));

		push({ ...basePush("job-1", 1), generationPhase: "Sampling", step: 4, totalSteps: 20 });
		push({ ...basePush("job-1", 2), generationPhase: "Sampling", step: 5, totalSteps: 20 });
		push({ ...basePush("job-1", 3), generationPhase: "Sampling", step: 6, totalSteps: 20 });

		expect(invalidateSpy).toHaveBeenCalledTimes(1);
	});

	it("refetches again on the terminal transition", async () => {
		const { invalidateSpy } = renderHub(["job-1"]);
		await waitFor(() => expect(registeredHandlers.has(IMAGE_JOB_STATUS_CHANGED)).toBe(true));

		push(basePush("job-1", 1));
		push({ ...basePush("job-1", 2), phase: "Succeeded" });

		expect(invalidateSpy).toHaveBeenCalledTimes(2);
	});

	// The load-bearing regression: the hook used to parse the push, dedupe on seq, then throw every other field away.
	// The generation timeline exists ONLY in the push (the runtime reads it off the daemon's stdout), so a discarded
	// payload means no amount of refetching can ever produce it.
	it("writes the generation timeline from the push into the cache", async () => {
		const { queryClient } = renderHub(["job-1"]);
		await waitFor(() => expect(registeredHandlers.has(IMAGE_JOB_STATUS_CHANGED)).toBe(true));

		push({
			...basePush("job-1", 1),
			generationPhase: "Sampling",
			step: 12,
			totalSteps: 20,
			secondsPerIteration: 2,
			estimatedRemainingMs: 16_000,
		});

		expect(queryClient.getQueryData(imageJobProgressKey("job-1"))).toMatchObject({
			generationPhase: "Sampling",
			step: 12,
			totalSteps: 20,
			estimatedRemainingMs: 16_000,
		});
	});

	it("dedupes a replayed push with a non-increasing seq", async () => {
		const { queryClient } = renderHub(["job-1"]);
		await waitFor(() => expect(registeredHandlers.has(IMAGE_JOB_STATUS_CHANGED)).toBe(true));

		push({ ...basePush("job-1", 2), generationPhase: "Sampling", step: 8, totalSteps: 20 });
		push({ ...basePush("job-1", 2), generationPhase: "Sampling", step: 3, totalSteps: 20 }); // duplicate seq — ignored
		push({ ...basePush("job-1", 1), generationPhase: "Sampling", step: 2, totalSteps: 20 }); // stale replay — ignored

		expect(queryClient.getQueryData(imageJobProgressKey("job-1"))).toMatchObject({ step: 8 });
	});

	it("tracks seq independently per job", async () => {
		const { invalidateSpy } = renderHub(["job-1", "job-2"]);
		await waitFor(() => expect(registeredHandlers.has(IMAGE_JOB_STATUS_CHANGED)).toBe(true));

		push(basePush("job-1", 5));
		push(basePush("job-2", 1)); // different job — its own seq baseline, must invalidate
		expect(invalidateSpy).toHaveBeenCalledTimes(2);
	});

	it("drops an unparseable push without invalidating", async () => {
		const { invalidateSpy } = renderHub(["job-1"]);
		await waitFor(() => expect(registeredHandlers.has(IMAGE_JOB_STATUS_CHANGED)).toBe(true));

		push({ nonsense: true });
		expect(invalidateSpy).not.toHaveBeenCalled();
	});

	it("stops the connection on unmount", async () => {
		const { unmount } = renderHub(["job-1"]);
		await waitFor(() => expect(startSpy).toHaveBeenCalled());
		// The shared manager stops on last release only AFTER a 30s stop-linger (reused across navigation); switch to
		// fake timers to advance past it deterministically instead of waiting real time.
		vi.useFakeTimers();
		try {
			unmount();
			await vi.advanceTimersByTimeAsync(30_000);
			expect(stopSpy).toHaveBeenCalled();
		} finally {
			vi.useRealTimers();
		}
	});
});

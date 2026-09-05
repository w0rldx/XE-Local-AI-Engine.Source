// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { RuntimeAcquisitionStatus } from "@/features/node-settings/queries/useLocalRuntime";

const { acquisitionQueryKey, hubMock, hydrateMock } = vi.hoisted(() => ({
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	acquisitionQueryKey: [{ _id: "getRuntimeAcquisitionStatus" }] as const,
	hubMock: {
		handler: undefined as ((payload: unknown) => void) | undefined,
		unregisterReconnected: vi.fn(),
		release: vi.fn(),
		acquire: vi.fn(),
		on: vi.fn((_name: string, handler: (payload: unknown) => void) => {
			hubMock.handler = handler;
		}),
		off: vi.fn(),
		onReconnected: vi.fn(() => hubMock.unregisterReconnected),
	},
	hydrateMock: { queryFn: vi.fn() },
}));

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: (path: string) => {
		hubMock.acquire(path);
		return {
			connection: { on: hubMock.on, off: hubMock.off },
			onReconnected: hubMock.onReconnected,
			release: hubMock.release,
		};
	},
}));

// Only the acquisition read is stubbed; every other generated binding stays real so `useLocalRuntime` still resolves.
// The stub deliberately keeps the REAL `useRuntimeAcquisitionStatus` wrapper — and therefore the real
// `structuralSharing` sequence guard — inside the path under test, since that guard is what these tests exist to prove.
vi.mock("@/core/api/generated/@tanstack/react-query.gen", async (importOriginal) => ({
	...(await importOriginal<typeof import("@/core/api/generated/@tanstack/react-query.gen")>()),
	getRuntimeAcquisitionStatusOptions: () => ({
		queryKey: acquisitionQueryKey,
		queryFn: hydrateMock.queryFn,
	}),
}));

import { useRuntimeAcquisitionHub } from "@/features/node-settings/hooks/useRuntimeAcquisitionHub";

function status(overrides: Partial<RuntimeAcquisitionStatus> & { sequence: number; phase: string }): RuntimeAcquisitionStatus {
	return { variant: "cpu", tag: "b9692", completedBytes: null, totalBytes: null, stepIndex: 1, stepCount: 1, ...overrides };
}

/** A hydrate response the test resolves by hand, so the GET can be made to land before OR after a push. */
function deferredHydrate(): { resolve: (value: RuntimeAcquisitionStatus) => void } {
	let resolve: (value: RuntimeAcquisitionStatus) => void = () => undefined;
	const promise = new Promise<RuntimeAcquisitionStatus>((resolveFn) => {
		resolve = resolveFn;
	});
	hydrateMock.queryFn.mockReturnValue(promise);
	return { resolve };
}

function renderHub(enabled = true) {
	// retry: false so a never-settling hydrate does not schedule background retries across tests.
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	const wrapper = ({ children }: { children: ReactNode }) => (
		<QueryClientProvider client={queryClient}>{children}</QueryClientProvider>
	);
	return { queryClient, ...renderHook(() => useRuntimeAcquisitionHub(enabled), { wrapper }) };
}

/**
 * Pushes a hub payload and lets the resulting render settle.
 *
 * real-timer: the trailing `setTimeout(…, 0)` is a macrotask YIELD, not a wait — it has no duration to get wrong and
 * no race to lose. TanStack delivers observer notifications through its own `setTimeout(…, 0)`, so the only way to
 * read the value the push produced is to queue behind that task; a read taken any earlier still sees the previous
 * value even though the cache write landed, and the "nothing changed" assertions would then pass for the wrong
 * reason. Fake timers cannot replace it here: `waitFor` deadlocks under them, and several callers assert a NEGATIVE
 * (no re-render, no refetch) which no `waitFor` can express.
 */
async function push(payload: unknown): Promise<void> {
	act(() => hubMock.handler?.(payload));
	await act(async () => {
		await new Promise((resolve) => setTimeout(resolve, 0));
	});
}

describe("useRuntimeAcquisitionHub", () => {
	beforeEach(() => {
		hubMock.handler = undefined;
		vi.clearAllMocks();
		hydrateMock.queryFn.mockReturnValue(new Promise(() => undefined));
	});

	it("hydrates on mount so a client that joined mid-acquisition still sees the banner", async () => {
		const hydrate = deferredHydrate();
		const { result } = renderHub();

		expect(result.current).toBeUndefined();
		hydrate.resolve(status({ sequence: 4, phase: "Downloading", completedBytes: 1024, totalBytes: 4096 }));

		await waitFor(() => expect(result.current?.phase).toBe("Downloading"));
		expect(result.current?.sequence).toBe(4);
		expect(hubMock.acquire).toHaveBeenCalledWith("model-fit/llamacpp/acquisition/hub");
	});

	it("advances on a push whose sequence beats the hydrated snapshot", async () => {
		const hydrate = deferredHydrate();
		const { result } = renderHub();

		hydrate.resolve(status({ sequence: 1, phase: "DetectingGpu" }));
		await waitFor(() => expect(result.current?.phase).toBe("DetectingGpu"));

		await push(status({ sequence: 2, phase: "Downloading", completedBytes: 512, totalBytes: 8192 }));

		await waitFor(() => expect(result.current?.phase).toBe("Downloading"));
		expect(result.current?.sequence).toBe(2);
	});

	it("keeps a terminal push when a stale hydrate response lands after it", async () => {
		// The failure mode the sequence guard exists for: the GET was issued before the acquisition failed, so it carries
		// an older sequence, yet it *arrives* after the terminal push. Taking the later arrival would put the banner back
		// into a download that has already ended, with nothing left to ever end it.
		const hydrate = deferredHydrate();
		const { queryClient, result } = renderHub();

		await push(status({ sequence: 9, phase: "Failed", sanitizedError: "The download failed." }));
		await waitFor(() => expect(result.current?.phase).toBe("Failed"));

		hydrate.resolve(status({ sequence: 8, phase: "Downloading", completedBytes: 2048, totalBytes: 8192 }));
		// Wait for the in-flight GET to actually settle into the query, so this asserts the response was applied and
		// rejected — not merely that it had not arrived yet.
		await waitFor(() => expect(queryClient.getQueryState(acquisitionQueryKey)?.fetchStatus).toBe("idle"));

		expect(result.current?.phase).toBe("Failed");
		expect(result.current?.sequence).toBe(9);
		expect(result.current?.sanitizedError).toBe("The download failed.");
	});

	it("drops an out-of-order push instead of rewinding the phase", async () => {
		const hydrate = deferredHydrate();
		const { queryClient, result } = renderHub();

		hydrate.resolve(status({ sequence: 5, phase: "Extracting" }));
		await waitFor(() => expect(result.current?.phase).toBe("Extracting"));

		// Equal sequence (a re-push of the same status) and a lower one are both rejected: only a strictly greater
		// sequence may advance the cache.
		await push(status({ sequence: 5, phase: "Downloading" }));
		await push(status({ sequence: 3, phase: "Downloading" }));

		expect(queryClient.getQueryData(acquisitionQueryKey)).toMatchObject({ sequence: 5, phase: "Extracting" });
		expect(result.current?.phase).toBe("Extracting");
	});

	it("drops a payload that fails the schema rather than caching a guessed shape", async () => {
		const hydrate = deferredHydrate();
		const { queryClient, result } = renderHub();

		hydrate.resolve(status({ sequence: 2, phase: "Verifying" }));
		await waitFor(() => expect(result.current?.phase).toBe("Verifying"));

		await push({ sequence: "99", phase: "Failed" });

		expect(queryClient.getQueryData(acquisitionQueryKey)).toMatchObject({ sequence: 2, phase: "Verifying" });
		expect(result.current?.phase).toBe("Verifying");
	});

	it("releases the shared lease and both registrations on unmount", () => {
		const { unmount } = renderHub();

		unmount();

		expect(hubMock.unregisterReconnected).toHaveBeenCalledOnce();
		expect(hubMock.off).toHaveBeenCalledOnce();
		expect(hubMock.release).toHaveBeenCalledOnce();
	});

	it("does not touch the hub or the endpoint before the client is authenticated", () => {
		renderHub(false);

		expect(hubMock.acquire).not.toHaveBeenCalled();
		expect(hydrateMock.queryFn).not.toHaveBeenCalled();
	});
});

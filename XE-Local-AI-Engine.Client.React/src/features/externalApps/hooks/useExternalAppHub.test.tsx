// @vitest-environment jsdom

// The WIRE-CONTRACT guard for `external-apps/hub`. Two things here fail SILENTLY rather than loudly when they drift,
// so both are asserted literally: a wrong SignalR method name is a no-op, not an error, and the ping's `kind` is
// lowerCamelCase on the hub while the REST feed spells the same enum PascalCase — which is safe only because this
// hook never reads it. A test that let the hook branch on `kind` would hide the day the two casings met.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { externalAppInvalidationKey, externalAppQueryIds } from "@/features/externalApps/queries/useExternalApps";
import { externalAppTestIds } from "@/features/externalApps/test/ExternalAppFixtures";

const hubMock = vi.hoisted(() => {
	const handlers = new Map<string, (message: unknown) => void>();
	const connection = {
		state: "Connected",
		on: vi.fn((event: string, handler: (message: unknown) => void) => handlers.set(event, handler)),
		off: vi.fn((event: string) => handlers.delete(event)),
		// The real `invoke` always returns a promise and the unmount path calls `.catch()` on it, so the seam must too.
		invoke: vi.fn(async (): Promise<unknown> => undefined),
	};
	const handle = {
		connection,
		whenStarted: Promise.resolve(),
		onReconnected: vi.fn(),
		onReconnecting: vi.fn(),
		onClosed: vi.fn(),
		release: vi.fn(),
	};
	return {
		acquire: vi.fn(() => handle),
		connection,
		handle,
		handlers,
		reconnect: undefined as (() => void) | undefined,
		reconnecting: undefined as (() => void) | undefined,
		closed: undefined as (() => void) | undefined,
	};
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: hubMock.acquire,
}));

import {
	EXTERNAL_APP_POLL_INTERVAL_MS,
	type ExternalAppSubscriptionSnapshot,
	useExternalAppHub,
} from "@/features/externalApps/hooks/useExternalAppHub";

const instanceId = externalAppTestIds.instance;

function snapshot(overrides: Partial<ExternalAppSubscriptionSnapshot> = {}): ExternalAppSubscriptionSnapshot {
	return {
		instanceId,
		status: "Installing",
		desiredState: "Running",
		failureCategory: null,
		lastSequence: 5,
		events: [],
		replayTruncated: false,
		...overrides,
	};
}

function harness(): { queryClient: QueryClient; wrapper: ({ children }: { children: ReactNode }) => ReactNode } {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	return {
		queryClient,
		wrapper: ({ children }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>,
	};
}

function emitChange(change: { instanceId: string; sequence: number; kind: string; status?: string }): void {
	act(() => hubMock.handlers.get("externalAppChanged")?.(change));
}

function emitPullProgress(progress: {
	instanceId: string;
	service: string;
	layerCount: number;
	completedLayers: number;
	bytes: number;
}): void {
	act(() => hubMock.handlers.get("externalAppPullProgress")?.(progress));
}

interface InvalidateSpy {
	readonly mock: { readonly calls: unknown[][] };
}

function invalidatedKeys(spy: InvalidateSpy): unknown[] {
	return spy.mock.calls.map((call) => (call[0] as { queryKey: unknown }).queryKey);
}

describe("useExternalAppHub", () => {
	beforeEach(() => {
		hubMock.handlers.clear();
		hubMock.connection.state = "Connected";
		hubMock.connection.on.mockClear();
		hubMock.connection.off.mockClear();
		hubMock.connection.invoke.mockReset();
		hubMock.handle.release.mockClear();
		hubMock.acquire.mockClear();
		hubMock.reconnect = undefined;
		hubMock.reconnecting = undefined;
		hubMock.closed = undefined;
		hubMock.handle.onReconnected.mockReset();
		hubMock.handle.onReconnected.mockImplementation((callback: () => void) => {
			hubMock.reconnect = callback;
			return vi.fn();
		});
		hubMock.handle.onReconnecting.mockReset();
		hubMock.handle.onReconnecting.mockImplementation((callback: () => void) => {
			hubMock.reconnecting = callback;
			return vi.fn();
		});
		hubMock.handle.whenStarted = Promise.resolve();
		hubMock.handle.onClosed.mockReset();
		hubMock.handle.onClosed.mockImplementation((callback: () => void) => {
			hubMock.closed = callback;
			return vi.fn();
		});
	});

	it("subscribes by name with two arguments and exposes the snapshot's desiredState and failureCategory", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ status: "Failed", failureCategory: "ImagePullFailed" }));
		const { wrapper } = harness();
		const { result } = renderHook(() => useExternalAppHub(instanceId), { wrapper });

		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		expect(hubMock.acquire).toHaveBeenCalledWith("external-apps/hub");
		expect(hubMock.connection.invoke).toHaveBeenCalledWith("Subscribe", instanceId, 0);
		expect(result.current.status).toBe("Failed");
		// Without it, "stopped because the operator asked" and "stopped and should be running" read identically.
		expect(result.current.desiredState).toBe("Running");
		expect(result.current.failureCategory).toBe("ImagePullFailed");
		expect(result.current.watermark).toBe(5);
		expect(result.current.pollIntervalMs).toBeUndefined();
	});

	it("refreshes every feed on a ping above the watermark and ignores one at or below it", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSequence: 5 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useExternalAppHub(instanceId), { wrapper });
		await waitFor(() => expect(result.current.watermark).toBe(5));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		emitChange({ instanceId, sequence: 6, kind: "started", status: "Running" });

		const keys = invalidatedKeys(invalidate);
		expect(keys).toContainEqual(externalAppInvalidationKey(externalAppQueryIds.instance, { instanceId }));
		expect(keys).toContainEqual(externalAppInvalidationKey(externalAppQueryIds.instances));
		expect(keys).toContainEqual(externalAppInvalidationKey(externalAppQueryIds.events, { instanceId }));
		expect(keys).toContainEqual(externalAppInvalidationKey(externalAppQueryIds.logs, { instanceId }));
		expect(result.current.watermark).toBe(6);

		// A replayed ping must not re-invalidate: the sequence itself is the dedupe.
		invalidate.mockClear();
		emitChange({ instanceId, sequence: 6, kind: "started", status: "Running" });
		emitChange({ instanceId, sequence: 2, kind: "installed", status: "Installing" });
		expect(invalidate).not.toHaveBeenCalled();
		expect(result.current.watermark).toBe(6);
	});

	// The hub spells the kind lowerCamelCase and the REST feed PascalCase. The hook reads neither casing, so a kind it
	// has never heard of still advances the watermark and still refreshes everything.
	it("never reads the ping's kind: an unknown lowerCamelCase kind still refreshes every feed", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSequence: 0 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useExternalAppHub(instanceId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		emitChange({ instanceId, sequence: 1, kind: "somethingANewerServerInvented", status: "Running" });

		expect(invalidatedKeys(invalidate)).toContainEqual(externalAppInvalidationKey(externalAppQueryIds.instance, { instanceId }));
		expect(result.current.watermark).toBe(1);
	});

	it("refreshes the runtime card once the status settles, and not while it is still busy", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSequence: 0 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useExternalAppHub(instanceId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		emitChange({ instanceId, sequence: 1, kind: "installed", status: "Installing" });
		expect(invalidatedKeys(invalidate)).not.toContainEqual(externalAppInvalidationKey(externalAppQueryIds.runtime));

		emitChange({ instanceId, sequence: 2, kind: "started", status: "Running" });
		expect(invalidatedKeys(invalidate)).toContainEqual(externalAppInvalidationKey(externalAppQueryIds.runtime));
	});

	it("buffers pings that arrive before the snapshot and replays them in sequence order", async () => {
		let resolveSnapshot: ((value: ExternalAppSubscriptionSnapshot) => void) | undefined;
		hubMock.connection.invoke.mockImplementation(
			async () =>
				await new Promise<ExternalAppSubscriptionSnapshot>((resolve) => {
					resolveSnapshot = resolve;
				}),
		);
		const { wrapper } = harness();
		const { result } = renderHook(() => useExternalAppHub(instanceId), { wrapper });
		await waitFor(() => expect(hubMock.handlers.has("externalAppChanged")).toBe(true));

		emitChange({ instanceId, sequence: 9, kind: "started", status: "Running" });
		emitChange({ instanceId, sequence: 7, kind: "installed", status: "Installing" });
		expect(result.current.watermark).toBe(0);

		await act(async () => {
			resolveSnapshot?.(snapshot({ lastSequence: 6 }));
			await Promise.resolve();
		});

		// Out of order on the wire, applied in order here: the later sequence must win the watermark.
		await waitFor(() => expect(result.current.watermark).toBe(9));
		expect(result.current.status).toBe("Running");
	});

	it("polls while the transport is down and stops only once a re-subscribe succeeds", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSequence: 5 }));
		const { wrapper } = harness();
		const { result } = renderHook(() => useExternalAppHub(instanceId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));

		act(() => hubMock.reconnecting?.());
		expect(result.current.pollIntervalMs).toBe(EXTERNAL_APP_POLL_INTERVAL_MS);

		act(() => hubMock.closed?.());
		expect(result.current.connectionState).toBe("unavailable");
		expect(result.current.pollIntervalMs).toBe(EXTERNAL_APP_POLL_INTERVAL_MS);

		act(() => hubMock.reconnect?.());
		await waitFor(() => expect(result.current.pollIntervalMs).toBeUndefined());
		// The watermark, not zero: everything the instance did while the transport was down arrives as replay.
		expect(hubMock.connection.invoke).toHaveBeenLastCalledWith("Subscribe", instanceId, 5);
	});

	it("keeps pull progress per service in local state and invalidates nothing for it", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSequence: 0 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useExternalAppHub(instanceId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		emitPullProgress({ instanceId, service: "web", layerCount: 4, completedLayers: 1, bytes: 1_024 });
		emitPullProgress({ instanceId, service: "worker", layerCount: 2, completedLayers: 2, bytes: 2_048 });
		emitPullProgress({ instanceId, service: "web", layerCount: 4, completedLayers: 3, bytes: 4_096 });

		expect(result.current.pullProgress["web"]?.completedLayers).toBe(3);
		expect(result.current.pullProgress["worker"]?.completedLayers).toBe(2);
		// A query per layer would turn a multi-gigabyte pull into a request storm.
		expect(invalidate).not.toHaveBeenCalled();
	});

	it("unsubscribes by name and releases the shared connection on unmount", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot());
		const { wrapper } = harness();
		const { result, unmount } = renderHook(() => useExternalAppHub(instanceId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));

		unmount();

		expect(hubMock.connection.invoke).toHaveBeenLastCalledWith("Unsubscribe", instanceId);
		expect(hubMock.handle.release).toHaveBeenCalledTimes(1);
		expect(hubMock.connection.off).toHaveBeenCalledWith("externalAppChanged", expect.any(Function));
		expect(hubMock.connection.off).toHaveBeenCalledWith("externalAppPullProgress", expect.any(Function));
	});

	// A transport that never came up is the same degraded case as one that dropped later. The manager swallows the
	// start error, so a hook that only waited for `whenStarted` sat on "connecting" forever with no poll behind it —
	// a frozen instance page with nothing saying the live feed was gone.
	it("falls back to polling when the initial start never connected", async () => {
		hubMock.connection.state = "Disconnected";
		hubMock.connection.invoke.mockResolvedValue(snapshot());
		const { wrapper } = harness();

		const { result } = renderHook(() => useExternalAppHub(instanceId), { wrapper });

		await waitFor(() => expect(result.current.connectionState).toBe("unavailable"));
		expect(result.current.pollIntervalMs).toBe(EXTERNAL_APP_POLL_INTERVAL_MS);
		// Nothing was subscribed: the invoke would have thrown on a disconnected connection.
		expect(hubMock.connection.invoke).not.toHaveBeenCalled();
	});

	it("falls back to polling when the start promise itself rejects", async () => {
		hubMock.connection.state = "Disconnected";
		hubMock.handle.whenStarted = Promise.reject(new Error("negotiate refused"));
		const { wrapper } = harness();

		const { result } = renderHook(() => useExternalAppHub(instanceId), { wrapper });

		await waitFor(() => expect(result.current.connectionState).toBe("unavailable"));
		expect(result.current.pollIntervalMs).toBe(EXTERNAL_APP_POLL_INTERVAL_MS);
	});

	it("acquires no connection at all without an instance id", () => {
		const { wrapper } = harness();
		const { result } = renderHook(() => useExternalAppHub(undefined), { wrapper });

		expect(hubMock.acquire).not.toHaveBeenCalled();
		expect(result.current.connectionState).toBe("idle");
	});
});

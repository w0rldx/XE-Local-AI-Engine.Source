// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Every handler the hooks register for the tick, in registration order (one per mounted subscriber).
const tickHandlers: ((...args: unknown[]) => void)[] = [];

const signalRMock = vi.hoisted(() => {
	const connection = {
		state: "Connected",
		on: vi.fn(),
		off: vi.fn(),
		onreconnected: vi.fn(),
		onreconnecting: vi.fn(),
		onclose: vi.fn(),
		start: vi.fn<() => Promise<void>>().mockResolvedValue(undefined),
		stop: vi.fn<() => Promise<void>>().mockResolvedValue(undefined),
	};
	const builder = {
		withUrl: vi.fn(),
		withAutomaticReconnect: vi.fn(),
		configureLogging: vi.fn(),
		build: vi.fn(),
	};
	builder.withUrl.mockReturnValue(builder);
	builder.withAutomaticReconnect.mockReturnValue(builder);
	builder.configureLogging.mockReturnValue(builder);
	builder.build.mockReturnValue(connection);
	return { builder, connection };
});

vi.mock("@microsoft/signalr", () => ({
	HubConnectionBuilder: vi.fn(function HubConnectionBuilder() {
		return signalRMock.builder;
	}),
	HubConnectionState: { Connected: "Connected", Disconnected: "Disconnected" },
	LogLevel: { Warning: 3 },
}));

import { resetSharedHubConnectionsForTest } from "@/core/api/signalr/SharedHubConnection";
import { useRuntimeResidencyHub } from "@/core/api/signalr/useRuntimeResidencyHub";

const EVENT = "runtimeResidency.changed";
const RUNNING_KEY = [{ _id: "listRunningModels" }];
const RESIDENTS_KEY = [{ _id: "getRuntimeResidents" }];

// queryKey of every invalidateQueries call, so a test can assert which query a tick or a (re)connect refreshed.
const invalidatedKeys: unknown[] = [];

function makeWrapper() {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	vi.spyOn(queryClient, "invalidateQueries").mockImplementation((filters) => {
		invalidatedKeys.push((filters as { queryKey?: unknown } | undefined)?.queryKey);
		return Promise.resolve();
	});
	return function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	};
}

function renderHub(enabled = true, queryKey: unknown[] = RUNNING_KEY) {
	return renderHook(() => useRuntimeResidencyHub(enabled, queryKey), { wrapper: makeWrapper() });
}

// Waits out the initial connect, whose one re-read is asserted separately, and clears the record.
async function renderConnectedHub() {
	const rendered = renderHub();
	await waitFor(() => expect(rendered.result.current.isLive).toBe(true));
	invalidatedKeys.length = 0;
	return rendered;
}

describe("useRuntimeResidencyHub", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		resetSharedHubConnectionsForTest();
		invalidatedKeys.length = 0;
		tickHandlers.length = 0;
		signalRMock.connection.on.mockImplementation((name: string, handler: (...args: unknown[]) => void) => {
			if (name === EVENT) {
				tickHandlers.push(handler);
			}
		});
		signalRMock.builder.withUrl.mockReturnValue(signalRMock.builder);
		signalRMock.builder.withAutomaticReconnect.mockReturnValue(signalRMock.builder);
		signalRMock.builder.configureLogging.mockReturnValue(signalRMock.builder);
		signalRMock.builder.build.mockReturnValue(signalRMock.connection);
		signalRMock.connection.start.mockResolvedValue(undefined);
		signalRMock.connection.stop.mockResolvedValue(undefined);
		signalRMock.connection.state = "Connected";
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("connects to the residency hub and re-reads its own query once connected", async () => {
		const { result } = renderHub();

		expect(signalRMock.builder.withUrl).toHaveBeenCalledWith(
			expect.stringContaining("/api/local/v1/model-fit/residency/hub"),
			expect.objectContaining({ accessTokenFactory: expect.any(Function) }),
		);
		expect(result.current.isLive).toBe(false);
		await waitFor(() => expect(result.current.isLive).toBe(true));
		expect(invalidatedKeys).toEqual([RUNNING_KEY]);
	});

	it("invalidates only the passed query on a tick", async () => {
		await renderConnectedHub();

		tickHandlers[0]?.({ sequence: 7 });

		expect(invalidatedKeys).toEqual([RUNNING_KEY]);
	});

	it("shares one connection between subscribers, each invalidating only its own query", async () => {
		const running = renderHub(true, RUNNING_KEY);
		const residents = renderHub(true, RESIDENTS_KEY);
		await waitFor(() => expect(running.result.current.isLive && residents.result.current.isLive).toBe(true));
		invalidatedKeys.length = 0;

		for (const handler of tickHandlers) {
			handler({ sequence: 1 });
		}

		expect(signalRMock.builder.build).toHaveBeenCalledTimes(1);
		expect(tickHandlers).toHaveLength(2);
		expect(invalidatedKeys).toEqual([RUNNING_KEY, RESIDENTS_KEY]);
	});

	it("drops a malformed tick", async () => {
		await renderConnectedHub();

		tickHandlers[0]?.({ sequence: "seven" });
		tickHandlers[0]?.(undefined);

		expect(invalidatedKeys).toEqual([]);
	});

	it("stays degraded when the hub never connects", async () => {
		signalRMock.connection.state = "Disconnected";
		const { result } = renderHub();

		await act(async () => {
			await signalRMock.connection.start.mock.results[0]?.value;
		});

		expect(result.current.isLive).toBe(false);
		expect(invalidatedKeys).toEqual([]);
	});

	it("degrades while reconnecting and goes live again with one re-read after the reconnect", async () => {
		const { result } = await renderConnectedHub();
		const onReconnecting = signalRMock.connection.onreconnecting.mock.calls[0]?.[0] as () => void;
		const onReconnected = signalRMock.connection.onreconnected.mock.calls[0]?.[0] as () => void;

		act(() => onReconnecting());
		expect(result.current.isLive).toBe(false);

		act(() => onReconnected());
		expect(result.current.isLive).toBe(true);
		expect(invalidatedKeys).toEqual([RUNNING_KEY]);
	});

	it("degrades for good once the connection closes", async () => {
		const { result } = await renderConnectedHub();
		const onClose = signalRMock.connection.onclose.mock.calls[0]?.[0] as () => void;

		act(() => onClose());

		expect(result.current.isLive).toBe(false);
	});

	it("opens no connection while disabled", () => {
		const { result } = renderHub(false);

		expect(signalRMock.builder.withUrl).not.toHaveBeenCalled();
		expect(signalRMock.connection.on).not.toHaveBeenCalled();
		expect(result.current.isLive).toBe(false);
	});

	it("unsubscribes and stops the connection on unmount", async () => {
		vi.useFakeTimers();
		try {
			const { unmount } = renderHub();

			unmount();

			expect(signalRMock.connection.off).toHaveBeenCalledWith(EVENT, expect.any(Function));
			// The shared manager stops on last release only after its 30s linger and once start() settles.
			await vi.advanceTimersByTimeAsync(30_000);
			expect(signalRMock.connection.stop).toHaveBeenCalled();
		} finally {
			vi.useRealTimers();
		}
	});
});

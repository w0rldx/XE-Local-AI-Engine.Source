// @vitest-environment jsdom

// The WIRE-CONTRACT guard for `development-workflows/hub`. Three things here are invisible failures rather than errors
// when they drift, so they are asserted literally: `SubscribeRun` takes TWO arguments (the second is the client's
// watermark, re-sent on every reconnect), `kind` is one of exactly four values, and those values are LOWERCASE — a
// `"Node"` would match no switch arm and silently stop invalidating anything.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { devWorkflowRunEvent, devWorkflowTestIds } from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { devWorkflowInvalidationKey, devWorkflowQueryIds } from "@/features/devWorkflows/queries/useDevWorkflows";

const hubMock = vi.hoisted(() => {
	const handlers = new Map<string, (change: unknown) => void>();
	const connection = {
		state: "Connected",
		on: vi.fn((event: string, handler: (change: unknown) => void) => handlers.set(event, handler)),
		off: vi.fn((event: string) => handlers.delete(event)),
		// The real SignalR `invoke` ALWAYS returns a promise, so the seam must too: the unsubscribe on unmount does
		// `.catch(...)` on the result, and a bare `vi.fn()` hands it `undefined`. `restoreMocks` cannot take this
		// implementation away — it calls `mockRestore` on `vi.spyOn` spies and never touches a plain `vi.fn()` — and
		// passing the implementation to `vi.fn` (rather than `mockResolvedValue`) survives `mockReset` too.
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
	DEV_WORKFLOW_POLL_INTERVAL_MS,
	type DevWorkflowRunSubscriptionSnapshot,
	useDevWorkflowRunHub,
} from "@/features/devWorkflows/hooks/useDevWorkflowRunHub";

const runId = devWorkflowTestIds.run;
const workItemId = devWorkflowTestIds.workItem;

function snapshot(overrides: Partial<DevWorkflowRunSubscriptionSnapshot> = {}): DevWorkflowRunSubscriptionSnapshot {
	return {
		runId,
		status: "Running",
		queuedNodeCount: 2,
		runningNodeCount: 1,
		pendingDecisionCount: 0,
		blockingGateNodeRunId: null,
		lastSeq: 5,
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

function emit(change: { runId: string; seq: number; kind: string }): void {
	act(() => hubMock.handlers.get("devWorkflowChanged")?.(change));
}

interface InvalidateSpy {
	readonly mock: { readonly calls: unknown[][] };
}

function invalidatedKeys(spy: InvalidateSpy): unknown[] {
	return spy.mock.calls.map((call) => (call[0] as { queryKey: unknown }).queryKey);
}

describe("useDevWorkflowRunHub", () => {
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
		hubMock.handle.onClosed.mockReset();
		hubMock.handle.onClosed.mockImplementation((callback: () => void) => {
			hubMock.closed = callback;
			return vi.fn();
		});
	});

	it("subscribes with two arguments and paints the snapshot counters", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot());
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunHub(runId, workItemId), { wrapper });

		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		expect(hubMock.acquire).toHaveBeenCalledWith("development-workflows/hub");
		expect(hubMock.connection.invoke).toHaveBeenCalledWith("SubscribeRun", runId, 0);
		expect(result.current.status).toBe("Running");
		expect(result.current.queuedNodeCount).toBe(2);
		expect(result.current.runningNodeCount).toBe(1);
		expect(result.current.watermark).toBe(5);
		expect(result.current.pollIntervalMs).toBeUndefined();
	});

	it("re-subscribes on reconnect with the UPDATED watermark, never a regressed one", async () => {
		hubMock.connection.invoke.mockResolvedValueOnce(snapshot({ lastSeq: 5 })).mockResolvedValueOnce(snapshot({ lastSeq: 2 }));
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunHub(runId, workItemId), { wrapper });
		await waitFor(() => expect(result.current.watermark).toBe(5));

		// Sequences are strictly increasing but NOT contiguous — the counter is shared with node-runs and artifacts, so
		// a jump from 5 to 21 is ordinary and must not be treated as a gap to reconcile.
		emit({ runId, seq: 21, kind: "node" });
		await waitFor(() => expect(result.current.watermark).toBe(21));

		act(() => hubMock.reconnect?.());

		await waitFor(() => expect(hubMock.connection.invoke).toHaveBeenCalledTimes(2));
		expect(hubMock.connection.invoke).toHaveBeenLastCalledWith("SubscribeRun", runId, 21);
		expect(result.current.watermark).toBe(21);
	});

	it("maps each of the four LOWERCASE kinds to its own feed, and every kind to the event feed", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 0 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunHub(runId, workItemId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		const expectations = [
			// X19's four kinds, verbatim. `graph` and `event` are deliberately absent: a materialization folds into
			// `node` (Y12) and every kind moves the event feed anyway.
			{ kind: "run", key: devWorkflowInvalidationKey(devWorkflowQueryIds.run, { runId }) },
			{ kind: "node", key: devWorkflowInvalidationKey(devWorkflowQueryIds.node, { runId }) },
			{ kind: "gate", key: devWorkflowInvalidationKey(devWorkflowQueryIds.node, { runId }) },
			{ kind: "artifact", key: devWorkflowInvalidationKey(devWorkflowQueryIds.artifacts, { runId }) },
		];

		let sequence = 1;
		for (const expectation of expectations) {
			invalidate.mockClear();
			emit({ runId, seq: sequence, kind: expectation.kind });
			sequence += 1;
			const keys = invalidatedKeys(invalidate);
			expect(keys, `kind "${expectation.kind}" invalidated ${JSON.stringify(keys)}`).toContainEqual(expectation.key);
			expect(keys).toContainEqual(devWorkflowInvalidationKey(devWorkflowQueryIds.events, { runId }));
		}
	});

	it("refreshes the work-item list on a run ping, because the list shows the run's status", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 0 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunHub(runId, workItemId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		emit({ runId, seq: 1, kind: "run" });

		const keys = invalidatedKeys(invalidate);
		expect(keys).toContainEqual(devWorkflowInvalidationKey(devWorkflowQueryIds.workItem, { workItemId }));
		expect(keys).toContainEqual(devWorkflowInvalidationKey(devWorkflowQueryIds.workItems));
	});

	it("treats a capitalised kind as unknown and refreshes everything rather than silently no-opping", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 0 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunHub(runId, workItemId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		emit({ runId, seq: 1, kind: "Node" });

		const keys = invalidatedKeys(invalidate);
		expect(keys).toContainEqual(devWorkflowInvalidationKey(devWorkflowQueryIds.run, { runId }));
		expect(keys).toContainEqual(devWorkflowInvalidationKey(devWorkflowQueryIds.artifacts, { runId }));
	});

	it("buffers pings that arrive before the snapshot resolves and applies them in sequence order", async () => {
		let resolveSnapshot: ((value: DevWorkflowRunSubscriptionSnapshot) => void) | undefined;
		hubMock.connection.invoke.mockReturnValue(
			new Promise<DevWorkflowRunSubscriptionSnapshot>((resolve) => {
				resolveSnapshot = resolve;
			}),
		);
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunHub(runId, workItemId), { wrapper });
		await waitFor(() => expect(hubMock.connection.invoke).toHaveBeenCalled());

		emit({ runId, seq: 9, kind: "node" });
		emit({ runId, seq: 7, kind: "node" });
		await act(async () => {
			resolveSnapshot?.(snapshot({ lastSeq: 5 }));
		});

		await waitFor(() => expect(result.current.watermark).toBe(9));
	});

	it("ignores duplicate sequences and other runs' pushes", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 0 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunHub(runId, workItemId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		emit({ runId, seq: 4, kind: "node" });
		invalidate.mockClear();
		emit({ runId, seq: 4, kind: "node" });
		emit({ runId: "77777777-7777-4777-8777-777777777777", seq: 5, kind: "node" });

		expect(invalidate).not.toHaveBeenCalled();
		expect(result.current.watermark).toBe(4);
	});

	it("refreshes every feed when the replay was truncated", async () => {
		hubMock.connection.invoke.mockResolvedValue(
			snapshot({ lastSeq: 400, events: [devWorkflowRunEvent()], replayTruncated: true }),
		);
		const { queryClient, wrapper } = harness();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();
		const { result } = renderHook(() => useDevWorkflowRunHub(runId, workItemId), { wrapper });

		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const keys = invalidatedKeys(invalidate);
		expect(keys).toContainEqual(devWorkflowInvalidationKey(devWorkflowQueryIds.run, { runId }));
		expect(keys).toContainEqual(devWorkflowInvalidationKey(devWorkflowQueryIds.artifacts, { runId }));
		expect(result.current.watermark).toBe(400);
	});

	it("starts polling when the transport drops AFTER a good subscribe — the live-run defect", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot());
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunHub(runId, workItemId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		expect(result.current.pollIntervalMs).toBeUndefined();

		act(() => hubMock.reconnecting?.());

		// Observed live: the page kept saying "connected" and painted frozen data for 70s with no alert and no network
		// activity, because only the subscribe's own catch could ever turn polling on.
		expect(result.current.connectionState).toBe("reconnecting");
		expect(result.current.pollIntervalMs).toBe(DEV_WORKFLOW_POLL_INTERVAL_MS);
	});

	it("re-subscribes with the watermark and stops polling once the transport comes back", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 5 }));
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunHub(runId, workItemId), { wrapper });
		await waitFor(() => expect(result.current.watermark).toBe(5));

		act(() => hubMock.reconnecting?.());
		expect(result.current.pollIntervalMs).toBe(DEV_WORKFLOW_POLL_INTERVAL_MS);

		act(() => hubMock.reconnect?.());

		await waitFor(() => expect(result.current.pollIntervalMs).toBeUndefined());
		// Nothing the run did while the transport was down may be skipped, so the replay resumes from the watermark.
		expect(hubMock.connection.invoke).toHaveBeenLastCalledWith("SubscribeRun", runId, 5);
		expect(result.current.connectionState).toBe("connected");
	});

	it("keeps polling for good once the retry policy gives up", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot());
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunHub(runId, workItemId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));

		act(() => hubMock.closed?.());

		// Nothing re-announces a closed connection, so this state has to stick rather than decay back to "connected".
		expect(result.current.connectionState).toBe("unavailable");
		expect(result.current.pollIntervalMs).toBe(DEV_WORKFLOW_POLL_INTERVAL_MS);
	});

	it("falls back to polling when the subscribe fails, rather than throwing", async () => {
		hubMock.connection.invoke.mockRejectedValue(new Error("hub down"));
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunHub(runId, workItemId), { wrapper });

		await waitFor(() => expect(result.current.connectionState).toBe("unavailable"));
		expect(result.current.pollIntervalMs).toBe(DEV_WORKFLOW_POLL_INTERVAL_MS);
	});

	it("unsubscribes before releasing the shared connection", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot());
		const { wrapper } = harness();
		const { result, unmount } = renderHook(() => useDevWorkflowRunHub(runId, workItemId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));

		unmount();

		expect(hubMock.connection.invoke).toHaveBeenLastCalledWith("UnsubscribeRun", runId);
		expect(hubMock.handle.release).toHaveBeenCalledTimes(1);
		expect(hubMock.connection.off).toHaveBeenCalledWith("devWorkflowChanged", expect.any(Function));
	});

	it("does not acquire a connection without a run id", () => {
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevWorkflowRunHub(undefined, workItemId), { wrapper });

		expect(hubMock.acquire).not.toHaveBeenCalled();
		expect(result.current.connectionState).toBe("idle");
	});
});

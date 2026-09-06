// @vitest-environment jsdom

// The WIRE-CONTRACT guard for `graph-workflows/hub`. Four things here are invisible failures rather than errors when
// they drift, so they are asserted literally: the hub path, the two method names, `SubscribeRun` taking TWO arguments
// (the second is the client's watermark, re-sent on every reconnect), and `kind` being one of exactly three LOWERCASE
// values — a `"Node"` would match no switch arm and silently stop invalidating anything.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { graphWorkflowInvalidationKey, graphWorkflowQueryIds } from "@/features/graphWorkflows/queries/useGraphWorkflows";
import { graphWorkflowRunEvent, graphWorkflowTestIds } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";

const hubMock = vi.hoisted(() => {
	const handlers = new Map<string, (change: unknown) => void>();
	const connection = {
		state: "Connected",
		on: vi.fn((event: string, handler: (change: unknown) => void) => handlers.set(event, handler)),
		off: vi.fn((event: string) => handlers.delete(event)),
		// The real SignalR `invoke` ALWAYS returns a promise, so the seam must too: the unsubscribe on unmount does
		// `.catch(...)` on the result, and a bare `vi.fn()` hands it `undefined`.
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
	GRAPH_WORKFLOW_POLL_INTERVAL_MS,
	type GraphWorkflowRunSubscriptionSnapshot,
	useGraphWorkflowRunHub,
} from "@/features/graphWorkflows/hooks/useGraphWorkflowRunHub";

const runId = graphWorkflowTestIds.run;

const events = graphWorkflowInvalidationKey(graphWorkflowQueryIds.events, { runId });
const run = graphWorkflowInvalidationKey(graphWorkflowQueryIds.run, { runId });
const nodes = graphWorkflowInvalidationKey(graphWorkflowQueryIds.node, { runId });
const runList = graphWorkflowInvalidationKey(graphWorkflowQueryIds.runs);

function snapshot(overrides: Partial<GraphWorkflowRunSubscriptionSnapshot> = {}): GraphWorkflowRunSubscriptionSnapshot {
	return {
		runId,
		status: "Running",
		queuedNodeCount: 2,
		runningNodeCount: 1,
		pendingDecisionCount: 0,
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
	act(() => hubMock.handlers.get("graphWorkflowChanged")?.(change));
}

interface InvalidateSpy {
	readonly mock: { readonly calls: unknown[][] };
}

function invalidatedKeys(spy: InvalidateSpy): unknown[] {
	return spy.mock.calls.map((call) => (call[0] as { queryKey: unknown }).queryKey);
}

describe("useGraphWorkflowRunHub", () => {
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

	it("subscribes on the hub path with two arguments and paints the snapshot counters", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot());
		const { wrapper } = harness();
		const { result } = renderHook(() => useGraphWorkflowRunHub(runId), { wrapper });

		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		expect(hubMock.acquire).toHaveBeenCalledWith("graph-workflows/hub");
		expect(hubMock.connection.invoke).toHaveBeenCalledWith("SubscribeRun", runId, 0);
		expect(result.current.status).toBe("Running");
		expect(result.current.queuedNodeCount).toBe(2);
		expect(result.current.runningNodeCount).toBe(1);
		expect(result.current.watermark).toBe(5);
		expect(result.current.pollIntervalMs).toBeUndefined();
	});

	it("maps each of the three LOWERCASE kinds to its own feeds, and every kind to the event trail", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 0 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useGraphWorkflowRunHub(runId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		// The publisher's three kinds, verbatim, with the EXACT key set each one drops. Exact rather than "contains":
		// an unrecognised kind falls through to the everything-branch, whose keys are a superset of every arm's, so a
		// `toContainEqual` assertion would pass on a capitalised literal that had silently stopped matching.
		const expectations = [
			{ kind: "run", keys: [events, run, runList] },
			{ kind: "node", keys: [events, run, nodes] },
			// A gate parks or releases the run, so the list's status column moves with the panel.
			{ kind: "gate", keys: [events, run, nodes, runList] },
		];

		let sequence = 1;
		for (const expectation of expectations) {
			invalidate.mockClear();
			emit({ runId, seq: sequence, kind: expectation.kind });
			sequence += 1;
			expect(invalidatedKeys(invalidate), `kind "${expectation.kind}"`).toEqual(expectation.keys);
		}
	});

	it("treats a capitalised kind as unknown and refreshes everything rather than silently no-opping", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 0 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useGraphWorkflowRunHub(runId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		emit({ runId, seq: 1, kind: "Node" });

		// Every feed, which is strictly more than any single arm drops — the trail twice, once unconditionally and once
		// as part of the sweep. That difference is what makes the per-kind assertions above falsifiable.
		expect(invalidatedKeys(invalidate)).toEqual([events, run, nodes, runList, events]);
	});

	it("re-subscribes on reconnect with the UPDATED watermark, never a regressed one", async () => {
		hubMock.connection.invoke.mockResolvedValueOnce(snapshot({ lastSeq: 5 })).mockResolvedValueOnce(snapshot({ lastSeq: 2 }));
		const { wrapper } = harness();
		const { result } = renderHook(() => useGraphWorkflowRunHub(runId), { wrapper });
		await waitFor(() => expect(result.current.watermark).toBe(5));

		// Sequences are strictly increasing but NOT contiguous — the run's counter is shared with its node runs, so a
		// jump from 5 to 21 is ordinary and must not be treated as a gap to reconcile.
		emit({ runId, seq: 21, kind: "node" });
		await waitFor(() => expect(result.current.watermark).toBe(21));

		act(() => hubMock.reconnect?.());

		await waitFor(() => expect(hubMock.connection.invoke).toHaveBeenCalledTimes(2));
		expect(hubMock.connection.invoke).toHaveBeenLastCalledWith("SubscribeRun", runId, 21);
		expect(result.current.watermark).toBe(21);
	});

	it("buffers pings that arrive before the snapshot resolves and applies them in sequence order", async () => {
		let resolveSnapshot: ((value: GraphWorkflowRunSubscriptionSnapshot) => void) | undefined;
		hubMock.connection.invoke.mockReturnValue(
			new Promise<GraphWorkflowRunSubscriptionSnapshot>((resolve) => {
				resolveSnapshot = resolve;
			}),
		);
		const { wrapper } = harness();
		const { result } = renderHook(() => useGraphWorkflowRunHub(runId), { wrapper });
		await waitFor(() => expect(hubMock.connection.invoke).toHaveBeenCalled());

		emit({ runId, seq: 9, kind: "node" });
		emit({ runId, seq: 7, kind: "node" });
		await act(async () => {
			resolveSnapshot?.(snapshot({ lastSeq: 5 }));
		});

		await waitFor(() => expect(result.current.watermark).toBe(9));
	});

	it("ignores a ping BELOW the watermark and other runs' pushes", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 0 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useGraphWorkflowRunHub(runId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		emit({ runId, seq: 4, kind: "node" });
		invalidate.mockClear();
		emit({ runId, seq: 3, kind: "node" });
		emit({ runId: "77777777-7777-4777-8777-777777777777", seq: 5, kind: "node" });

		expect(invalidate).not.toHaveBeenCalled();
		expect(result.current.watermark).toBe(4);
	});

	it("still re-reads on a ping that REPEATS the watermark — the terminal cancel", async () => {
		// Live: the node table reached `Cancelled` and the run badge sat on `Cancelling` for 90 s while `GET runs/{id}`
		// already said `Cancelled`. `Cancelling → Cancelled` writes no event, so the store reports the run's current
		// sequence and the ping repeats the watermark; a `<=` gate dropped the only notification of the terminal state.
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 5 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useGraphWorkflowRunHub(runId), { wrapper });
		await waitFor(() => expect(result.current.watermark).toBe(5));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		emit({ runId, seq: 5, kind: "run" });

		expect(invalidatedKeys(invalidate)).toEqual([events, run, runList]);
		expect(result.current.watermark).toBe(5);

		invalidate.mockClear();
		emit({ runId, seq: 4, kind: "run" });

		expect(invalidate).not.toHaveBeenCalled();
	});

	it("refreshes every feed when the replay was truncated", async () => {
		hubMock.connection.invoke.mockResolvedValue(
			snapshot({ lastSeq: 200, events: [graphWorkflowRunEvent()], replayTruncated: true }),
		);
		const { queryClient, wrapper } = harness();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();
		const { result } = renderHook(() => useGraphWorkflowRunHub(runId), { wrapper });

		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const keys = invalidatedKeys(invalidate);
		expect(keys).toContainEqual(graphWorkflowInvalidationKey(graphWorkflowQueryIds.run, { runId }));
		expect(keys).toContainEqual(graphWorkflowInvalidationKey(graphWorkflowQueryIds.events, { runId }));
		expect(result.current.watermark).toBe(200);
	});

	it("starts polling when the transport drops AFTER a good subscribe", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot());
		const { wrapper } = harness();
		const { result } = renderHook(() => useGraphWorkflowRunHub(runId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		expect(result.current.pollIntervalMs).toBeUndefined();

		act(() => hubMock.reconnecting?.());

		// Observed live on the dev-workflow hub this is copied from: the page kept saying "connected" and painted frozen
		// data with no alert and no network activity, because only the subscribe's own catch could turn polling on.
		expect(result.current.connectionState).toBe("reconnecting");
		expect(result.current.pollIntervalMs).toBe(GRAPH_WORKFLOW_POLL_INTERVAL_MS);
	});

	it("clears the poll ONLY once a re-subscribe has actually succeeded", async () => {
		hubMock.connection.invoke.mockResolvedValueOnce(snapshot({ lastSeq: 5 })).mockRejectedValueOnce(new Error("hub down"));
		const { wrapper } = harness();
		const { result } = renderHook(() => useGraphWorkflowRunHub(runId), { wrapper });
		await waitFor(() => expect(result.current.watermark).toBe(5));

		act(() => hubMock.reconnecting?.());
		expect(result.current.pollIntervalMs).toBe(GRAPH_WORKFLOW_POLL_INTERVAL_MS);

		// A reconnect whose subscribe FAILS is not a recovery: the poll has to stay on.
		act(() => hubMock.reconnect?.());
		await waitFor(() => expect(result.current.connectionState).toBe("unavailable"));
		expect(result.current.pollIntervalMs).toBe(GRAPH_WORKFLOW_POLL_INTERVAL_MS);

		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 9 }));
		act(() => hubMock.reconnect?.());

		await waitFor(() => expect(result.current.pollIntervalMs).toBeUndefined());
		// Nothing the run did while the transport was down may be skipped, so the replay resumes from the watermark.
		expect(hubMock.connection.invoke).toHaveBeenLastCalledWith("SubscribeRun", runId, 5);
		expect(result.current.connectionState).toBe("connected");
	});

	it("keeps polling for good once the retry policy gives up", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot());
		const { wrapper } = harness();
		const { result } = renderHook(() => useGraphWorkflowRunHub(runId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));

		act(() => hubMock.closed?.());

		// Nothing re-announces a closed connection, so this state has to stick rather than decay back to "connected".
		expect(result.current.connectionState).toBe("unavailable");
		expect(result.current.pollIntervalMs).toBe(GRAPH_WORKFLOW_POLL_INTERVAL_MS);
	});

	it("falls back to polling when the first subscribe fails, rather than throwing", async () => {
		hubMock.connection.invoke.mockRejectedValue(new Error("hub down"));
		const { wrapper } = harness();
		const { result } = renderHook(() => useGraphWorkflowRunHub(runId), { wrapper });

		await waitFor(() => expect(result.current.connectionState).toBe("unavailable"));
		expect(result.current.pollIntervalMs).toBe(GRAPH_WORKFLOW_POLL_INTERVAL_MS);
	});

	it("unsubscribes before releasing the shared connection", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot());
		const { wrapper } = harness();
		const { result, unmount } = renderHook(() => useGraphWorkflowRunHub(runId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));

		unmount();

		expect(hubMock.connection.invoke).toHaveBeenLastCalledWith("UnsubscribeRun", runId);
		expect(hubMock.handle.release).toHaveBeenCalledTimes(1);
		expect(hubMock.connection.off).toHaveBeenCalledWith("graphWorkflowChanged", expect.any(Function));
	});

	it("does not acquire a connection without a run id", () => {
		const { wrapper } = harness();
		const { result } = renderHook(() => useGraphWorkflowRunHub(undefined), { wrapper });

		expect(hubMock.acquire).not.toHaveBeenCalled();
		expect(result.current.connectionState).toBe("idle");
	});
});

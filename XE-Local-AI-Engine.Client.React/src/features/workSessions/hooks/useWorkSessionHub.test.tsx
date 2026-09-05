// @vitest-environment jsdom

// The WIRE-CONTRACT guard for `work-sessions/hub`. Two things here are invisible failures rather than errors when
// they drift, so they are asserted literally: `SubscribeSession` takes TWO arguments (the second is the client's
// watermark, re-sent on every reconnect), and `kind` is LOWERCASE on the wire — a `"Status"` would match no switch
// arm and silently stop invalidating.

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";
import type { WorkSessionEventResponse } from "@/features/workSessions/models/WorkSessionModels";
import { workSessionInvalidationKey, workSessionQueryIds } from "@/features/workSessions/queries/useWorkSessions";

const hubMock = vi.hoisted(() => {
	const handlers = new Map<string, (change: unknown) => void>();
	const connection = {
		state: "Connected",
		on: vi.fn((event: string, handler: (change: unknown) => void) => handlers.set(event, handler)),
		off: vi.fn((event: string) => handlers.delete(event)),
		// The real SignalR `invoke` ALWAYS returns a promise, so the seam must too: the unsubscribe on unmount does
		// `.catch(...)` on the result, and a bare `vi.fn()` hands it `undefined`. Passing the implementation to `vi.fn`
		// (rather than `mockResolvedValue`) keeps it through `mockReset`/`restoreMocks`, which restore that argument.
		invoke: vi.fn(async (): Promise<unknown> => undefined),
	};
	const handle = { connection, whenStarted: Promise.resolve(), onReconnected: vi.fn(), release: vi.fn() };
	return { acquire: vi.fn(() => handle), connection, handle, handlers, reconnect: undefined as (() => void) | undefined };
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: hubMock.acquire,
}));

import {
	useWorkSessionHub,
	WORK_SESSION_POLL_INTERVAL_MS,
	type WorkSessionSubscriptionSnapshot,
} from "@/features/workSessions/hooks/useWorkSessionHub";

const sessionId = "11111111-1111-4111-8111-111111111111";
const conversationId = "22222222-2222-4222-8222-222222222222";

function replayedEvent(sequence: number): WorkSessionEventResponse {
	return { id: `event-${sequence}`, sequence, step: 1, eventType: "StepStarted", occurredAtUtc: sequence };
}

function snapshot(overrides: Partial<WorkSessionSubscriptionSnapshot> = {}): WorkSessionSubscriptionSnapshot {
	return {
		sessionId,
		status: "Running",
		step: 3,
		currentTaskId: "task-1",
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

function emit(change: { sessionId: string; seq: number; kind: string }): void {
	act(() => hubMock.handlers.get("workSessionChanged")?.(change));
}

interface InvalidateSpy {
	readonly mock: { readonly calls: unknown[][] };
}

function invalidatedKeys(spy: InvalidateSpy): unknown[] {
	return spy.mock.calls.map((call) => (call[0] as { queryKey: unknown }).queryKey);
}

describe("useWorkSessionHub", () => {
	beforeEach(() => {
		hubMock.handlers.clear();
		hubMock.connection.state = "Connected";
		hubMock.connection.on.mockClear();
		hubMock.connection.off.mockClear();
		hubMock.connection.invoke.mockReset();
		hubMock.handle.release.mockClear();
		hubMock.acquire.mockClear();
		hubMock.reconnect = undefined;
		hubMock.handle.onReconnected.mockReset();
		hubMock.handle.onReconnected.mockImplementation((callback: () => void) => {
			hubMock.reconnect = callback;
			return vi.fn();
		});
	});

	it("subscribes with two arguments and paints the snapshot header", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot());
		const { wrapper } = harness();
		const { result } = renderHook(() => useWorkSessionHub(sessionId, conversationId), { wrapper });

		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		expect(hubMock.acquire).toHaveBeenCalledWith("work-sessions/hub");
		expect(hubMock.connection.invoke).toHaveBeenCalledWith("SubscribeSession", sessionId, 0);
		expect(result.current.status).toBe("Running");
		expect(result.current.step).toBe(3);
		expect(result.current.currentTaskId).toBe("task-1");
		expect(result.current.watermark).toBe(5);
		expect(result.current.pollIntervalMs).toBeUndefined();
	});

	it("re-subscribes on reconnect with the UPDATED watermark, never a regressed one", async () => {
		hubMock.connection.invoke.mockResolvedValueOnce(snapshot({ lastSeq: 5 })).mockResolvedValueOnce(snapshot({ lastSeq: 2 }));
		const { wrapper } = harness();
		const { result } = renderHook(() => useWorkSessionHub(sessionId, conversationId), { wrapper });
		await waitFor(() => expect(result.current.watermark).toBe(5));

		emit({ sessionId, seq: 9, kind: "task" });
		await waitFor(() => expect(result.current.watermark).toBe(9));

		act(() => hubMock.reconnect?.());

		await waitFor(() => expect(hubMock.connection.invoke).toHaveBeenCalledTimes(2));
		expect(hubMock.connection.invoke).toHaveBeenLastCalledWith("SubscribeSession", sessionId, 9);
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		expect(result.current.watermark).toBe(9);
	});

	it("maps every LOWERCASE kind to its own feed, and every kind to the event feed", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 0 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useWorkSessionHub(sessionId, conversationId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		const expectations = [
			{ kind: "status", key: workSessionInvalidationKey(workSessionQueryIds.get, sessionId) },
			{ kind: "task", key: workSessionInvalidationKey(workSessionQueryIds.tasks, sessionId) },
			{ kind: "finding", key: workSessionInvalidationKey(workSessionQueryIds.findings, sessionId) },
			{ kind: "artifact", key: workSessionInvalidationKey(workSessionQueryIds.artifacts, sessionId) },
			{ kind: "checkpoint", key: workSessionInvalidationKey(workSessionQueryIds.checkpoints, sessionId) },
		];

		let sequence = 1;
		for (const expectation of expectations) {
			invalidate.mockClear();
			emit({ sessionId, seq: sequence, kind: expectation.kind });
			sequence += 1;
			const keys = invalidatedKeys(invalidate);
			expect(keys, `kind "${expectation.kind}" invalidated ${JSON.stringify(keys)}`).toContainEqual(expectation.key);
			// Every kind moves the append-only event feed.
			expect(keys).toContainEqual(workSessionInvalidationKey(workSessionQueryIds.events, sessionId));
		}

		// A `status` change also refreshes the list page's rows.
		invalidate.mockClear();
		emit({ sessionId, seq: sequence, kind: "status" });
		expect(invalidatedKeys(invalidate)).toContainEqual(workSessionInvalidationKey(workSessionQueryIds.list));
	});

	it("treats a capitalised kind as unknown and refreshes everything rather than silently no-opping", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 0 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useWorkSessionHub(sessionId, conversationId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		emit({ sessionId, seq: 1, kind: "Status" });

		const keys = invalidatedKeys(invalidate);
		expect(keys).toContainEqual(workSessionInvalidationKey(workSessionQueryIds.get, sessionId));
		expect(keys).toContainEqual(workSessionInvalidationKey(workSessionQueryIds.tasks, sessionId));
	});

	it("invalidates the conversation and bumps the resume nonce on a step push", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 0 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useWorkSessionHub(sessionId, conversationId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();
		const nonceBefore = result.current.resumeNonce;

		emit({ sessionId, seq: 1, kind: "step" });

		expect(invalidatedKeys(invalidate)).toContainEqual(nodeChatQueryKeys.conversation(conversationId));
		await waitFor(() => expect(result.current.resumeNonce).toBe(nonceBefore + 1));
	});

	it("ignores duplicate sequences and other sessions' pushes", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 0 }));
		const { queryClient, wrapper } = harness();
		const { result } = renderHook(() => useWorkSessionHub(sessionId, conversationId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		emit({ sessionId, seq: 4, kind: "step" });
		invalidate.mockClear();
		emit({ sessionId, seq: 4, kind: "step" });
		emit({ sessionId: "33333333-3333-4333-8333-333333333333", seq: 5, kind: "step" });

		expect(invalidate).not.toHaveBeenCalled();
		expect(result.current.watermark).toBe(4);
		expect(result.current.resumeNonce).toBe(1);
	});

	it("refreshes every feed when the replay was truncated", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot({ lastSeq: 400, events: [replayedEvent(1)], replayTruncated: true }));
		const { queryClient, wrapper } = harness();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();
		const { result } = renderHook(() => useWorkSessionHub(sessionId, conversationId), { wrapper });

		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const keys = invalidatedKeys(invalidate);
		expect(keys).toContainEqual(workSessionInvalidationKey(workSessionQueryIds.events, sessionId));
		expect(keys).toContainEqual(workSessionInvalidationKey(workSessionQueryIds.tasks, sessionId));
		expect(result.current.watermark).toBe(400);
	});

	it("falls back to polling when the subscribe fails", async () => {
		hubMock.connection.invoke.mockRejectedValue(new Error("hub down"));
		const { wrapper } = harness();
		const { result } = renderHook(() => useWorkSessionHub(sessionId, conversationId), { wrapper });

		await waitFor(() => expect(result.current.connectionState).toBe("unavailable"));
		expect(result.current.pollIntervalMs).toBe(WORK_SESSION_POLL_INTERVAL_MS);
	});

	it("unsubscribes before releasing the shared connection", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot());
		const { wrapper } = harness();
		const { result, unmount } = renderHook(() => useWorkSessionHub(sessionId, conversationId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));

		unmount();

		expect(hubMock.connection.invoke).toHaveBeenLastCalledWith("UnsubscribeSession", sessionId);
		expect(hubMock.handle.release).toHaveBeenCalledTimes(1);
		expect(hubMock.connection.off).toHaveBeenCalledWith("workSessionChanged", expect.any(Function));
	});

	it("does not acquire a connection without a session id", () => {
		const { wrapper } = harness();
		const { result } = renderHook(() => useWorkSessionHub(undefined, undefined), { wrapper });

		expect(hubMock.acquire).not.toHaveBeenCalled();
		expect(result.current.connectionState).toBe("idle");
	});
});

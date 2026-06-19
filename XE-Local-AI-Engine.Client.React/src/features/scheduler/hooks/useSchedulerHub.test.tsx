// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Captured event handlers keyed by the SignalR client-method name the hook subscribes to.
const handlers = new Map<string, (...args: unknown[]) => void>();

const signalRMock = vi.hoisted(() => {
	const connection = {
		on: vi.fn(),
		off: vi.fn(),
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
	LogLevel: { Warning: 3 },
}));

import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { useSchedulerHub } from "@/features/scheduler/hooks/useSchedulerHub";
import { schedulerInvalidationKey, schedulerQueryIds } from "@/features/scheduler/queries/useScheduler";

const RUN_EVENTS = [
	"scheduler.runStarted",
	"scheduler.runCompleted",
	"scheduler.runFailed",
	"scheduler.runCancelled",
	"scheduler.runProgress",
];

// The query-invalidation bridge: the hub must invalidate the generated query keys, which are single-element arrays
// `[{ _id: "<operationId>", ... }]`. A job-definition push invalidates listScheduledJobs; a run push invalidates
// both listScheduledJobRuns and getScheduledJobRun. Invalidation is by the `_id` partial object (TanStack
// partial-object matching), so it matches every cached variant regardless of the query/path the call carried.
// These expected keys are built via the same production helper the hub uses.
const JOBS_KEY = schedulerInvalidationKey(schedulerQueryIds.listJobs);
const RUNS_KEY = schedulerInvalidationKey(schedulerQueryIds.listRuns);
const RUN_KEY = schedulerInvalidationKey(schedulerQueryIds.getRun);

// queryKey of every invalidateQueries call, so a test can assert the partial `_id` key shape the hub passed.
const invalidatedKeys: unknown[] = [];

function renderHub() {
	invalidatedKeys.length = 0;
	handlers.clear();
	signalRMock.connection.on.mockImplementation((name: string, handler: (...args: unknown[]) => void) => {
		handlers.set(name, handler);
	});
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	vi.spyOn(queryClient, "invalidateQueries").mockImplementation((filters) => {
		invalidatedKeys.push((filters as { queryKey?: unknown } | undefined)?.queryKey);
		return Promise.resolve();
	});
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	return { ...renderHook(() => useSchedulerHub(), { wrapper: Wrapper }), queryClient };
}

// Seeds a real query whose key matches the generated full-key shape `[{ _id, ... }]`, then drives an event and
// asserts TanStack actually marked it stale — proving the hub's partial `_id` invalidation reaches a live query
// keyed off the full generated options object (not just that invalidateQueries was called with a key).
function renderHubWithSeededQuery(fullQueryKey: readonly unknown[]) {
	handlers.clear();
	signalRMock.connection.on.mockImplementation((name: string, handler: (...args: unknown[]) => void) => {
		handlers.set(name, handler);
	});
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, gcTime: Number.POSITIVE_INFINITY } } });
	queryClient.setQueryData(fullQueryKey as unknown[], { items: [] });
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	renderHook(() => useSchedulerHub(), { wrapper: Wrapper });
	return queryClient;
}

describe("useSchedulerHub", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		useNodeAuthStore.getState().actions.clear();
		signalRMock.builder.withUrl.mockReturnValue(signalRMock.builder);
		signalRMock.builder.withAutomaticReconnect.mockReturnValue(signalRMock.builder);
		signalRMock.builder.configureLogging.mockReturnValue(signalRMock.builder);
		signalRMock.builder.build.mockReturnValue(signalRMock.connection);
		signalRMock.connection.start.mockResolvedValue(undefined);
		signalRMock.connection.stop.mockResolvedValue(undefined);
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("connects to the scheduler hub with the access-token factory and auto-reconnect", () => {
		useNodeAuthStore.getState().actions.setToken({ accessToken: "sched-token", expiresAtUtc: "2026-06-03T12:00:00Z" });
		renderHub();

		expect(signalRMock.builder.withUrl).toHaveBeenCalledWith(
			expect.stringContaining("/api/local/v1/scheduler/hub"),
			expect.objectContaining({ accessTokenFactory: expect.any(Function) }),
		);
		expect(signalRMock.builder.withUrl.mock.calls[0]?.[1].accessTokenFactory()).toBe("sched-token");
		expect(signalRMock.builder.withAutomaticReconnect).toHaveBeenCalled();
		expect(signalRMock.connection.start).toHaveBeenCalled();
	});

	it("subscribes to the definition-changed event and all five run events", () => {
		renderHub();

		expect(signalRMock.connection.on).toHaveBeenCalledWith("scheduler.jobDefinitionChanged", expect.any(Function));
		for (const eventName of RUN_EVENTS) {
			expect(signalRMock.connection.on).toHaveBeenCalledWith(eventName, expect.any(Function));
		}
	});

	it("invalidates the generated listScheduledJobs key on a definition-changed event", () => {
		renderHub();

		handlers.get("scheduler.jobDefinitionChanged")?.();

		expect(invalidatedKeys).toContainEqual(JOBS_KEY);
	});

	it("invalidates the generated run keys on every run event", () => {
		for (const eventName of RUN_EVENTS) {
			renderHub();
			handlers.get(eventName)?.();
			expect(invalidatedKeys).toContainEqual(RUNS_KEY);
			expect(invalidatedKeys).toContainEqual(RUN_KEY);
		}
	});

	it("marks a seeded jobs query stale via the partial `_id` match (the invalidation bridge end-to-end)", () => {
		// A real query keyed off the FULL generated options shape `[{ _id, ..., query }]` — the partial `_id`
		// invalidation must still reach it.
		// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
		const fullJobsKey = [{ _id: "listScheduledJobs", query: { includeDeleted: false } }];
		const queryClient = renderHubWithSeededQuery(fullJobsKey);

		handlers.get("scheduler.jobDefinitionChanged")?.();

		expect(queryClient.getQueryState(fullJobsKey)?.isInvalidated).toBe(true);
	});

	it("marks a seeded run query stale via the partial `_id` match (the invalidation bridge end-to-end)", () => {
		// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
		const fullRunsKey = [{ _id: "listScheduledJobRuns", query: { status: "Running" } }];
		const queryClient = renderHubWithSeededQuery(fullRunsKey);

		handlers.get("scheduler.runStarted")?.();

		expect(queryClient.getQueryState(fullRunsKey)?.isInvalidated).toBe(true);
	});

	it("unsubscribes and stops the connection on unmount", async () => {
		const { unmount } = renderHub();

		unmount();

		expect(signalRMock.connection.off).toHaveBeenCalledWith("scheduler.jobDefinitionChanged", expect.any(Function));
		for (const eventName of RUN_EVENTS) {
			expect(signalRMock.connection.off).toHaveBeenCalledWith(eventName, expect.any(Function));
		}
		// stop() is deferred until start() settles (so cleanup never aborts an in-flight negotiation), so it runs on a
		// microtask after unmount rather than synchronously.
		await vi.waitFor(() => expect(signalRMock.connection.stop).toHaveBeenCalled());
	});
});

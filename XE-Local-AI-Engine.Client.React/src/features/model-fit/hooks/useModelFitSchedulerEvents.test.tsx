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
		onreconnected: vi.fn(),
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

// Mock the toast helper so the hook's feedback is asserted at the call boundary (no real Mantine DOM toasts).
vi.mock("@/core/ui/notifications/Toast", () => ({
	toast: { error: vi.fn(), success: vi.fn(), warn: vi.fn() },
}));

// Deterministic i18n: t echoes the key so toast assertions are stable without an i18n provider.
vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (key: string) => key }),
}));

// Mock the imperative run-history fetch the on-connect catch-up uses (no real network).
vi.mock("@/features/scheduler/queries/useScheduler", () => ({
	fetchScheduledJobRuns: vi.fn().mockResolvedValue([]),
}));

import type { ScheduledJobRun } from "@/features/scheduler/models/SchedulerModels";
import { toast } from "@/core/ui/notifications/Toast";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { useModelFitSchedulerEvents } from "@/features/model-fit/hooks/useModelFitSchedulerEvents";
import { modelFitInvalidationKey, modelFitQueryIds } from "@/features/model-fit/queries/useModelFit";
import { fetchScheduledJobRuns } from "@/features/scheduler/queries/useScheduler";

const TERMINAL_RUN_EVENTS = ["scheduler.runCompleted", "scheduler.runFailed", "scheduler.runCancelled"];

const MODEL_FIT_TEMPLATE_ID = "model-recommendation-check";

// The §7.6 bridge: the hub invalidates the generated latest-recommendations key, a single-element array
// `[{ _id: "getLatestRecommendations", ... }]`. Invalidation is by the `_id` partial object (TanStack
// partial-object matching), so it matches every cached (useCase, providerName) variant. Built via the same
// production helper the hook uses.
const LATEST_KEY = modelFitInvalidationKey(modelFitQueryIds.latest);

const invalidatedKeys: unknown[] = [];

function renderHub(scheduledJobId?: string) {
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
	return renderHook(() => useModelFitSchedulerEvents(scheduledJobId), { wrapper: Wrapper });
}

// Seeds a real query whose key matches the generated full-key shape `[{ _id, ..., query }]`, then drives an event
// and asserts TanStack actually marked it stale — proving the hub's partial `_id` invalidation reaches a live
// query keyed off the full generated options object (not just that invalidateQueries was called with a key).
function renderHubWithSeededQuery(fullQueryKey: readonly unknown[]) {
	handlers.clear();
	signalRMock.connection.on.mockImplementation((name: string, handler: (...args: unknown[]) => void) => {
		handlers.set(name, handler);
	});
	const queryClient = new QueryClient({
		defaultOptions: { queries: { retry: false, gcTime: Number.POSITIVE_INFINITY } },
	});
	queryClient.setQueryData(fullQueryKey as unknown[], { hasCache: false, recommendations: [] });
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	renderHook(() => useModelFitSchedulerEvents(), { wrapper: Wrapper });
	return queryClient;
}

describe("useModelFitSchedulerEvents", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		useNodeAuthStore.getState().actions.clear();
		signalRMock.builder.withUrl.mockReturnValue(signalRMock.builder);
		signalRMock.builder.withAutomaticReconnect.mockReturnValue(signalRMock.builder);
		signalRMock.builder.configureLogging.mockReturnValue(signalRMock.builder);
		signalRMock.builder.build.mockReturnValue(signalRMock.connection);
		signalRMock.connection.start.mockResolvedValue(undefined);
		signalRMock.connection.stop.mockResolvedValue(undefined);
		vi.mocked(fetchScheduledJobRuns).mockResolvedValue([]);
	});

	afterEach(() => {
		vi.clearAllMocks();
	});

	it("connects to the scheduler hub with the access-token factory and auto-reconnect", () => {
		useNodeAuthStore.getState().actions.setToken({ accessToken: "mf-token", expiresAtUtc: "2026-06-03T12:00:00Z" });
		renderHub();

		expect(signalRMock.builder.withUrl).toHaveBeenCalledWith(
			expect.stringContaining("/api/local/v1/scheduler/hub"),
			expect.objectContaining({ accessTokenFactory: expect.any(Function) }),
		);
		expect(signalRMock.builder.withUrl.mock.calls[0]?.[1].accessTokenFactory()).toBe("mf-token");
		expect(signalRMock.builder.withAutomaticReconnect).toHaveBeenCalled();
		expect(signalRMock.connection.start).toHaveBeenCalled();
	});

	it("subscribes to only the three terminal run events (not started/progress)", () => {
		renderHub();

		for (const eventName of TERMINAL_RUN_EVENTS) {
			expect(signalRMock.connection.on).toHaveBeenCalledWith(eventName, expect.any(Function));
		}
		expect(signalRMock.connection.on).not.toHaveBeenCalledWith("scheduler.runStarted", expect.any(Function));
		expect(signalRMock.connection.on).not.toHaveBeenCalledWith("scheduler.runProgress", expect.any(Function));
	});

	it("invalidates the generated latest key on a terminal model-recommendation-check run", () => {
		renderHub();

		handlers.get("scheduler.runCompleted")?.({ templateId: MODEL_FIT_TEMPLATE_ID });

		expect(invalidatedKeys).toContainEqual(LATEST_KEY);
	});

	it("ignores terminal runs for other templates", () => {
		renderHub();

		handlers.get("scheduler.runCompleted")?.({ templateId: "some-other-template" });

		expect(invalidatedKeys).not.toContainEqual(LATEST_KEY);
	});

	it("ignores a run event with no payload", () => {
		renderHub();

		handlers.get("scheduler.runFailed")?.(undefined);

		expect(invalidatedKeys).not.toContainEqual(LATEST_KEY);
	});

	it("marks a seeded latest query stale via the partial `_id` match (the §7.6 bridge end-to-end)", () => {
		// A real query keyed off the FULL generated key shape `[{ _id, baseURL, query }]` (createQueryKey bakes in
		// the pinned baseURL: "" too) — the partial `_id` invalidation must still reach it.
		const fullLatestKey = [
			{
				// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
				_id: "getLatestRecommendations",
				baseURL: "",
				query: { useCase: "coding", providerName: "ollama" },
			},
		];
		const queryClient = renderHubWithSeededQuery(fullLatestKey);

		handlers.get("scheduler.runCompleted")?.({ templateId: MODEL_FIT_TEMPLATE_ID });

		expect(queryClient.getQueryState(fullLatestKey)?.isInvalidated).toBe(true);
	});

	it("shows a sticky error toast carrying the sanitized reason on a model-fit runFailed", () => {
		renderHub();

		handlers.get("scheduler.runFailed")?.({
			templateId: MODEL_FIT_TEMPLATE_ID,
			errorMessage: "The approved image is disabled.",
			runId: "run-1",
		});

		expect(toast.error).toHaveBeenCalledWith(
			"The approved image is disabled.",
			expect.objectContaining({ autoClose: false, title: "pages.modelFit.recommendations.toasts.failTitle" }),
		);
	});

	it("shows a brief success toast on a model-fit runCompleted", () => {
		renderHub();

		handlers.get("scheduler.runCompleted")?.({ templateId: MODEL_FIT_TEMPLATE_ID, runId: "run-2" });

		expect(toast.success).toHaveBeenCalledWith(
			"pages.modelFit.recommendations.toasts.success",
			expect.objectContaining({ autoClose: 5000 }),
		);
	});

	it("raises no toast for a terminal run of another template", () => {
		renderHub();

		handlers.get("scheduler.runFailed")?.({ templateId: "some-other-template", errorMessage: "boom" });

		expect(toast.error).not.toHaveBeenCalled();
		expect(toast.success).not.toHaveBeenCalled();
		expect(toast.warn).not.toHaveBeenCalled();
	});

	it("falls back to the i18n body when runFailed carries no errorMessage, still sticky", () => {
		renderHub();

		handlers.get("scheduler.runFailed")?.({ templateId: MODEL_FIT_TEMPLATE_ID, runId: "run-3" });

		expect(toast.error).toHaveBeenCalledWith(
			"pages.modelFit.recommendations.toasts.failFallback",
			expect.objectContaining({ autoClose: false }),
		);
	});

	it("unsubscribes and stops the connection on unmount", async () => {
		const { unmount } = renderHub();

		unmount();

		for (const eventName of TERMINAL_RUN_EVENTS) {
			expect(signalRMock.connection.off).toHaveBeenCalledWith(eventName, expect.any(Function));
		}
		// stop() is deferred until start() settles (so cleanup never aborts an in-flight negotiation), so it runs on a
		// microtask after unmount rather than synchronously.
		await vi.waitFor(() => expect(signalRMock.connection.stop).toHaveBeenCalled());
	});

	it("does not rebuild the connection on re-render (t is read via ref, not an effect dependency)", () => {
		// Regression guard: if `t` (or any unstable value) became an effect dependency, every react-i18next re-render
		// would tear down and rebuild the SignalR connection mid-negotiation ("stopped during negotiation"), leaving the
		// hub permanently disconnected so no run events arrive. The mocked useTranslation returns a fresh `t` each render.
		const { rerender } = renderHub();
		const startCallsAfterMount = signalRMock.connection.start.mock.calls.length;

		rerender();
		rerender();

		expect(signalRMock.connection.start.mock.calls.length).toBe(startCallsAfterMount);
	});

	it("on connect, catches up a missed terminal run via a single REST fetch and toasts it (no polling)", async () => {
		const missedRun = {
			id: "run-catchup-1",
			status: "Failed",
			actualFireTimeUtc: 1_900_000_000_000,
			errorMessage: "The approved image is disabled.",
		} as unknown as ScheduledJobRun;
		vi.mocked(fetchScheduledJobRuns).mockResolvedValueOnce([missedRun]);

		renderHub("job-mf");

		// start() resolving = the hub connected; the hook runs ONE catch-up fetch (no interval) and toasts the result.
		await vi.waitFor(() =>
			expect(toast.error).toHaveBeenCalledWith(
				"The approved image is disabled.",
				expect.objectContaining({ autoClose: false }),
			),
		);
		expect(vi.mocked(fetchScheduledJobRuns)).toHaveBeenCalledTimes(1);
		expect(vi.mocked(fetchScheduledJobRuns)).toHaveBeenCalledWith(
			expect.anything(),
			expect.objectContaining({ scheduledJobId: "job-mf" }),
		);
	});

	it("does not run the catch-up fetch when no job id is supplied (push-only)", async () => {
		renderHub();

		// Let start()'s resolution + the catch-up's early-return microtasks flush.
		await Promise.resolve();
		await Promise.resolve();

		expect(vi.mocked(fetchScheduledJobRuns)).not.toHaveBeenCalled();
	});
});

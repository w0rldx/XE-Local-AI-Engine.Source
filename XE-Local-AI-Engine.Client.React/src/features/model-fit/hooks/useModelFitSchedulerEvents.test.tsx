// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { modelFitQueryKeys } from "@/features/model-fit/queries/ModelFitQueryKeys";

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
import { useModelFitSchedulerEvents } from "@/features/model-fit/hooks/useModelFitSchedulerEvents";

const TERMINAL_RUN_EVENTS = ["scheduler.runCompleted", "scheduler.runFailed", "scheduler.runCancelled"];

const MODEL_FIT_TEMPLATE_ID = "model-recommendation-check";

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
	return renderHook(() => useModelFitSchedulerEvents(), { wrapper: Wrapper });
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

	it("invalidates the latest cache on a terminal model-recommendation-check run", () => {
		renderHub();

		handlers.get("scheduler.runCompleted")?.({ templateId: MODEL_FIT_TEMPLATE_ID });

		expect(invalidatedKeys).toContainEqual(modelFitQueryKeys.latestRoot());
	});

	it("ignores terminal runs for other templates", () => {
		renderHub();

		handlers.get("scheduler.runCompleted")?.({ templateId: "some-other-template" });

		expect(invalidatedKeys).not.toContainEqual(modelFitQueryKeys.latestRoot());
	});

	it("ignores a run event with no payload", () => {
		renderHub();

		handlers.get("scheduler.runFailed")?.(undefined);

		expect(invalidatedKeys).not.toContainEqual(modelFitQueryKeys.latestRoot());
	});

	it("unsubscribes and stops the connection on unmount", () => {
		const { unmount } = renderHub();

		unmount();

		for (const eventName of TERMINAL_RUN_EVENTS) {
			expect(signalRMock.connection.off).toHaveBeenCalledWith(eventName, expect.any(Function));
		}
		expect(signalRMock.connection.stop).toHaveBeenCalled();
	});
});

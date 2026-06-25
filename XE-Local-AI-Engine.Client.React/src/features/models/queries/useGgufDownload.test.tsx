// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Captured SignalR client-method handlers keyed by method name the hook subscribes to.
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

// The hydrate query returns an empty list by default so the test isolates the live-push path.
const getGgufDownloadsOptionsMock = vi.fn(() => ({
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	queryKey: [{ _id: "getGgufDownloads" }],
	queryFn: () => Promise.resolve({ items: [] }),
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	browseGgufRepositoriesOptions: vi.fn(),
	cancelGgufDownloadMutation: vi.fn(),
	getGgufDownloadsOptions: () => getGgufDownloadsOptionsMock(),
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	getGgufDownloadsQueryKey: () => [{ _id: "getGgufDownloads" }],
	inspectGgufRepositoryOptions: vi.fn(),
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	listLocalModelsQueryKey: () => [{ _id: "listLocalModels" }],
	startGgufDownloadMutation: vi.fn(),
}));

import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { useActiveGgufDownloads } from "@/features/models/queries/useGgufDownload";
import { useGgufBrowseStore } from "@/features/models/stores/GgufBrowseStore";

const STATUS_CHANGED = "ggufDownload.statusChanged";

function renderActiveDownloads(enabled = true) {
	handlers.clear();
	signalRMock.connection.on.mockImplementation((name: string, handler: (...args: unknown[]) => void) => {
		handlers.set(name, handler);
	});
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	return { ...renderHook(() => useActiveGgufDownloads({ enabled }), { wrapper: Wrapper }), queryClient };
}

describe("useActiveGgufDownloads", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		useNodeAuthStore.getState().actions.clear();
		useNodeAuthStore.getState().actions.setToken({ accessToken: "node-token", expiresAtUtc: "2026-06-26T12:00:00Z" });
		useGgufBrowseStore.setState({ browseQuery: "", inFlightDownloads: [] });
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

	it("opens the download hub with the access-token factory and subscribes to the status-changed event", () => {
		renderActiveDownloads();

		expect(signalRMock.builder.withUrl).toHaveBeenCalledWith(
			expect.stringContaining("/api/local/v1/model-fit/gguf/downloads/hub"),
			expect.objectContaining({ accessTokenFactory: expect.any(Function) }),
		);
		expect(signalRMock.builder.withUrl.mock.calls[0]?.[1].accessTokenFactory()).toBe("node-token");
		expect(signalRMock.builder.withAutomaticReconnect).toHaveBeenCalled();
		expect(signalRMock.connection.on).toHaveBeenCalledWith(STATUS_CHANGED, expect.any(Function));
		expect(signalRMock.connection.start).toHaveBeenCalled();
	});

	it("does NOT open the hub when disabled (pre-auth gate)", () => {
		renderActiveDownloads(false);

		expect(signalRMock.connection.start).not.toHaveBeenCalled();
		expect(signalRMock.connection.on).not.toHaveBeenCalled();
	});

	it("merges a Running push into the map with a computed percent and marks the model in-flight", () => {
		const { result } = renderActiveDownloads();

		act(() => {
			handlers.get(STATUS_CHANGED)?.({
				modelName: "unsloth/x:Q4_K_M",
				phase: "Running",
				completedBytes: 50,
				totalBytes: 200,
				sanitizedError: null,
			});
		});

		const status = result.current.get("unsloth/x:Q4_K_M");
		expect(status?.phase).toBe("Running");
		expect(status?.pct).toBe(25);
		expect(useGgufBrowseStore.getState().inFlightDownloads).toContain("unsloth/x:Q4_K_M");
	});

	it("removes the model from in-flight on a terminal (Completed) push", () => {
		const { result } = renderActiveDownloads();

		act(() => {
			handlers.get(STATUS_CHANGED)?.({
				modelName: "unsloth/x:Q4_K_M",
				phase: "Running",
				completedBytes: 10,
				totalBytes: 20,
				sanitizedError: null,
			});
		});
		expect(useGgufBrowseStore.getState().inFlightDownloads).toContain("unsloth/x:Q4_K_M");

		act(() => {
			handlers.get(STATUS_CHANGED)?.({
				modelName: "unsloth/x:Q4_K_M",
				phase: "Completed",
				completedBytes: 20,
				totalBytes: 20,
				sanitizedError: null,
			});
		});

		expect(result.current.get("unsloth/x:Q4_K_M")?.phase).toBe("Completed");
		expect(useGgufBrowseStore.getState().inFlightDownloads).not.toContain("unsloth/x:Q4_K_M");
	});

	it("invalidates the installed-models list once on a Completed push so the new model appears without a refresh", () => {
		const { queryClient } = renderActiveDownloads();
		const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		act(() => {
			handlers.get(STATUS_CHANGED)?.({
				modelName: "unsloth/x:Q4_K_M",
				phase: "Completed",
				completedBytes: 20,
				totalBytes: 20,
				sanitizedError: null,
			});
		});

		const invalidatedListKeys = invalidateSpy.mock.calls.filter(
			([filter]) => (filter?.queryKey as readonly { _id?: string }[] | undefined)?.[0]?._id === "listLocalModels",
		);
		expect(invalidatedListKeys).toHaveLength(1);

		// A re-pushed Completed status (hub reconnect / hydrate refetch) must not trigger another refetch.
		act(() => {
			handlers.get(STATUS_CHANGED)?.({
				modelName: "unsloth/x:Q4_K_M",
				phase: "Completed",
				completedBytes: 20,
				totalBytes: 20,
				sanitizedError: null,
			});
		});

		const invalidatedAgain = invalidateSpy.mock.calls.filter(
			([filter]) => (filter?.queryKey as readonly { _id?: string }[] | undefined)?.[0]?._id === "listLocalModels",
		);
		expect(invalidatedAgain).toHaveLength(1);
	});

	it("does NOT invalidate the installed-models list while a download is only Running", () => {
		const { queryClient } = renderActiveDownloads();
		const invalidateSpy = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();

		act(() => {
			handlers.get(STATUS_CHANGED)?.({
				modelName: "unsloth/x:Q4_K_M",
				phase: "Running",
				completedBytes: 10,
				totalBytes: 20,
				sanitizedError: null,
			});
		});

		const invalidatedListKeys = invalidateSpy.mock.calls.filter(
			([filter]) => (filter?.queryKey as readonly { _id?: string }[] | undefined)?.[0]?._id === "listLocalModels",
		);
		expect(invalidatedListKeys).toHaveLength(0);
	});

	it("surfaces a Failed push with its sanitized error and an undefined percent", () => {
		const { result } = renderActiveDownloads();

		act(() => {
			handlers.get(STATUS_CHANGED)?.({
				modelName: "unsloth/x:Q4_K_M",
				phase: "Failed",
				completedBytes: null,
				totalBytes: null,
				sanitizedError: "Download failed.",
			});
		});

		const status = result.current.get("unsloth/x:Q4_K_M");
		expect(status?.phase).toBe("Failed");
		expect(status?.pct).toBeUndefined();
		expect(status?.sanitizedError).toBe("Download failed.");
	});

	it("stops the connection on unmount", async () => {
		const { unmount } = renderActiveDownloads();

		unmount();

		expect(signalRMock.connection.off).toHaveBeenCalledWith(STATUS_CHANGED, expect.any(Function));
		await vi.waitFor(() => expect(signalRMock.connection.stop).toHaveBeenCalled());
	});
});

// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

// Captured SignalR client-method handlers keyed by method name the hook subscribes to.
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

// The hydrate query returns an empty list by default so the test isolates the live-push path.
const getGgufDownloadsOptionsMock = vi.fn(() => ({
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	queryKey: [{ _id: "getGgufDownloads" }],
	queryFn: () => Promise.resolve({ items: [] }),
}));
interface ImportStatusFixture {
	operationId: string;
	operationKind: string;
	modelName: string;
	phase: string;
	startedAtUtc: string;
	updatedAtUtc: string;
}

const getGgufImportsOptionsMock = vi.fn(() => ({
	// biome-ignore lint/style/useNamingConvention: generated query key discriminator.
	queryKey: [{ _id: "getGgufImports" }],
	queryFn: (): Promise<{ items: ImportStatusFixture[] }> => Promise.resolve({ items: [] }),
}));

vi.mock("@/core/api/generated/@tanstack/react-query.gen", () => ({
	browseGgufRepositoriesOptions: vi.fn(),
	cancelGgufDownloadMutation: vi.fn(),
	getGgufDownloadsOptions: () => getGgufDownloadsOptionsMock(),
	getGgufImportsOptions: () => getGgufImportsOptionsMock(),
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	getGgufDownloadsQueryKey: () => [{ _id: "getGgufDownloads" }],
	inspectGgufRepositoryOptions: vi.fn(),
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	listLocalModelsQueryKey: () => [{ _id: "listLocalModels" }],
	startGgufDownloadMutation: vi.fn(),
}));

import { resetSharedHubConnectionsForTest } from "@/core/api/signalr/SharedHubConnection";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import {
	toGgufAcquisitionStatus,
	type GgufAcquisitionStatus,
} from "@/features/models/models/GgufAcquisitionModels";
import { useActiveGgufDownloads } from "@/features/models/queries/useGgufDownload";
import {
	ACQUISITION_TERMINAL_RETENTION_LIMIT,
	mergeStatuses,
	pruneAcquisitionStatuses,
	pruneCompletedHandled,
	useActiveGgufAcquisitions,
} from "@/features/models/queries/useGgufAcquisitions";
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

function renderActiveAcquisitions() {
	handlers.clear();
	signalRMock.connection.on.mockImplementation((name: string, handler: (...args: unknown[]) => void) => {
		handlers.set(name, handler);
	});
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	function Wrapper({ children }: { children: ReactNode }) {
		return <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>;
	}
	return renderHook(() => useActiveGgufAcquisitions(), { wrapper: Wrapper });
}

function acquisitionStatus(
	operationId: string,
	phase: GgufAcquisitionStatus["phase"],
	updatedAtUtc: string,
): GgufAcquisitionStatus {
	return {
		operationId,
		operationKind: "Import",
		modelName: `${operationId}:Q4_K_M`,
		phase,
		pct: undefined,
		completedBytes: null,
		totalBytes: null,
		startedAtUtc: updatedAtUtc,
		updatedAtUtc,
		errorCode: null,
		sanitizedMessage: null,
	};
}

describe("useActiveGgufDownloads", () => {
	beforeEach(() => {
		vi.clearAllMocks();
		resetSharedHubConnectionsForTest();
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
				operationId: "11111111-1111-1111-1111-111111111111",
				operationKind: "Download",
				modelName: "unsloth/x:Q4_K_M",
				phase: "Running",
				completedBytes: 50,
				totalBytes: 200,
				sanitizedError: null,
			});
		});

		const status = result.current.get("unsloth/x:Q4_K_M");
		expect(status?.phase).toBe("Downloading");
		expect(status?.pct).toBe(25);
		expect(useGgufBrowseStore.getState().inFlightDownloads).toContain("unsloth/x:Q4_K_M");
	});

	it("removes the model from in-flight on a terminal (Completed) push", () => {
		const { result } = renderActiveDownloads();

		act(() => {
			handlers.get(STATUS_CHANGED)?.({
				operationId: "11111111-1111-1111-1111-111111111111",
				operationKind: "Download",
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
				operationId: "11111111-1111-1111-1111-111111111111",
				operationKind: "Download",
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
				operationId: "11111111-1111-1111-1111-111111111111",
				operationKind: "Download",
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
				operationId: "11111111-1111-1111-1111-111111111111",
				operationKind: "Download",
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
				operationId: "11111111-1111-1111-1111-111111111111",
				operationKind: "Download",
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
				operationId: "11111111-1111-1111-1111-111111111111",
				operationKind: "Download",
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

	it("hydrates acquisition-neutral imports by operation id and keeps them out of the download-only wrapper", async () => {
		getGgufImportsOptionsMock.mockReturnValueOnce({
			// biome-ignore lint/style/useNamingConvention: generated query key discriminator.
			queryKey: [{ _id: "getGgufImports" }],
			queryFn: () =>
				Promise.resolve({
					items: [
						{
							operationId: "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
							operationKind: "Import",
							modelName: "private:Q4_K_M",
							phase: "Copying",
							startedAtUtc: "2026-08-14T10:00:00Z",
							updatedAtUtc: "2026-08-14T10:00:01Z",
						},
					],
				}),
		});
		const acquisitions = renderActiveAcquisitions();
		await waitFor(() => expect(acquisitions.result.current.get("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa")?.modelName).toBe("private:Q4_K_M"));
		acquisitions.unmount();

		const downloads = renderActiveDownloads();
		act(() => {
			handlers.get(STATUS_CHANGED)?.({
				operationId: "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
				operationKind: "Import",
				modelName: "private:Q4_K_M",
				phase: "Copying",
			});
		});
		expect(downloads.result.current.has("private:Q4_K_M")).toBe(false);
	});

	it("maps legacy Running imports to acquisition-neutral copying rather than downloading", () => {
		const status = toGgufAcquisitionStatus({
			operationId: "legacy-import",
			operationKind: "Import",
			modelName: "private:Q4_K_M",
			phase: "Running",
		});

		expect(status?.phase).toBe("Copying");
	});

	it("ignores stale live and REST statuses after a newer terminal update", () => {
		const operationId = "monotonic-import";
		const completed = acquisitionStatus(operationId, "Completed", "2026-08-14T10:00:02Z");
		const delayedRunning = acquisitionStatus(operationId, "Copying", "2026-08-14T10:00:01Z");
		const afterLive = mergeStatuses(new Map(), [completed], Date.parse("2026-08-14T10:00:03Z"));

		const afterDelayedLive = mergeStatuses(afterLive, [delayedRunning], Date.parse("2026-08-14T10:00:03Z"));
		const afterStaleRest = mergeStatuses(afterDelayedLive, [delayedRunning], Date.parse("2026-08-14T10:00:03Z"));

		expect(afterDelayedLive.get(operationId)?.phase).toBe("Completed");
		expect(afterStaleRest.get(operationId)?.phase).toBe("Completed");
	});

	it("does not regress a terminal live status when a delayed Running push arrives", () => {
		// Terminal statuses are pruned 24h after updatedAtUtc against the real clock, so pin only Date (timers stay
		// real) — otherwise this test rots one day after its fixed timestamps.
		vi.useFakeTimers({ toFake: ["Date"], now: Date.parse("2026-08-14T12:00:00Z") });
		try {
			const { result } = renderActiveAcquisitions();
			const base = {
				operationId: "live-monotonic-import",
				operationKind: "Import",
				modelName: "private:Q4_K_M",
			};

			act(() => {
				handlers.get(STATUS_CHANGED)?.({ ...base, phase: "Completed", updatedAtUtc: "2026-08-14T10:00:02Z" });
				handlers.get(STATUS_CHANGED)?.({ ...base, phase: "Running", updatedAtUtc: "2026-08-14T10:00:01Z" });
			});

			expect(result.current.get(base.operationId)?.phase).toBe("Completed");
		} finally {
			vi.useRealTimers();
		}
	});

	it("bounds terminal acquisition and completed-handled retention without evicting active operations", () => {
		const now = Date.parse("2026-08-14T12:00:00Z");
		const statuses = new Map<string, GgufAcquisitionStatus>();
		statuses.set("active", acquisitionStatus("active", "Copying", "2026-08-10T00:00:00Z"));
		statuses.set("expired", acquisitionStatus("expired", "Failed", "2026-08-13T11:59:59Z"));
		for (let index = 0; index <= ACQUISITION_TERMINAL_RETENTION_LIMIT; index += 1) {
			const id = `terminal-${index.toString().padStart(3, "0")}`;
			statuses.set(id, acquisitionStatus(id, "Completed", new Date(now - index * 1000).toISOString()));
		}

		const prunedStatuses = pruneAcquisitionStatuses(statuses, now);
		expect(prunedStatuses.has("active")).toBe(true);
		expect(prunedStatuses.has("expired")).toBe(false);
		expect(prunedStatuses.has("terminal-000")).toBe(true);
		expect(prunedStatuses.has(`terminal-${ACQUISITION_TERMINAL_RETENTION_LIMIT}`)).toBe(false);
		expect([...prunedStatuses.values()].filter((status) => status.phase === "Completed")).toHaveLength(
			ACQUISITION_TERMINAL_RETENTION_LIMIT,
		);

		const handled = new Map<string, number>([["expired", now - 24 * 60 * 60 * 1000 - 1]]);
		for (let index = 0; index <= ACQUISITION_TERMINAL_RETENTION_LIMIT; index += 1) {
			handled.set(`handled-${index.toString().padStart(3, "0")}`, now - index);
		}
		const prunedHandled = pruneCompletedHandled(handled, now);
		expect(prunedHandled).toHaveLength(ACQUISITION_TERMINAL_RETENTION_LIMIT);
		expect(prunedHandled.has("expired")).toBe(false);
		expect(prunedHandled.has("handled-000")).toBe(true);
		expect(prunedHandled.has(`handled-${ACQUISITION_TERMINAL_RETENTION_LIMIT}`)).toBe(false);
	});

	it("stops the connection on unmount", async () => {
		vi.useFakeTimers();
		try {
			const { unmount } = renderActiveDownloads();

			unmount();

			expect(signalRMock.connection.off).toHaveBeenCalledWith(STATUS_CHANGED, expect.any(Function));
			// The shared manager stops on last release only AFTER a 30s stop-linger (reused across navigation) and once
			// start() settles; advance past the linger and flush the deferred-stop microtask.
			await vi.advanceTimersByTimeAsync(30_000);
			expect(signalRMock.connection.stop).toHaveBeenCalled();
		} finally {
			vi.useRealTimers();
		}
	});
});

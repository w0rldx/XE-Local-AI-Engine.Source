// @vitest-environment jsdom

import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { act, renderHook, waitFor } from "@testing-library/react";
import type { ReactNode } from "react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type {
	DevelopmentAttemptLiveUpdate,
	DevelopmentAttemptSubscriptionSnapshot,
} from "@/features/development/models/DevelopmentModels";
import { developmentInvalidationKey, developmentQueryIds } from "@/features/development/queries/useDevelopment";

const hubMock = vi.hoisted(() => {
	const handlers = new Map<string, (update: DevelopmentAttemptLiveUpdate) => void>();
	const connection = {
		state: "Connected",
		on: vi.fn((event: string, handler: (update: DevelopmentAttemptLiveUpdate) => void) => handlers.set(event, handler)),
		off: vi.fn((event: string) => handlers.delete(event)),
		invoke: vi.fn(),
	};
	const handle = {
		connection,
		whenStarted: Promise.resolve(),
		onReconnected: vi.fn(),
		release: vi.fn(),
	};
	return { acquire: vi.fn(() => handle), connection, handle, handlers, reconnect: undefined as (() => void) | undefined };
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: hubMock.acquire,
}));

import { useDevelopmentAttemptHub } from "@/features/development/hooks/useDevelopmentAttemptHub";

const projectId = "project-1";
const taskId = "task-1";
const attemptId = "attempt-1";

function update(sequence: number, overrides: Partial<DevelopmentAttemptLiveUpdate> = {}): DevelopmentAttemptLiveUpdate {
	return {
		projectId,
		taskId,
		attemptId,
		sequence,
		occurredAtUtc: sequence,
		kind: "Output",
		role: "Coder",
		status: "Running",
		modelId: "coder-model",
		provider: "local",
		outputDelta: `output-${sequence}`,
		providerRoundCount: 1,
		toolCallCount: 0,
		commandCount: 0,
		changedFileCount: 0,
		patchByteCount: 0,
		secondsSinceMeaningfulProgress: 0,
		...overrides,
	};
}

function snapshot(watermark: number, latest: DevelopmentAttemptLiveUpdate | null = null): DevelopmentAttemptSubscriptionSnapshot {
	return { projectId, taskId, attemptId, watermark, droppedOrCoalescedUpdateCount: 3, latest };
}

function deferred<T>(): { readonly promise: Promise<T>; readonly resolve: (value: T) => void } {
	let resolve!: (value: T) => void;
	const promise = new Promise<T>((complete) => {
		resolve = complete;
	});
	return { promise, resolve };
}

function harness(): {
	readonly queryClient: QueryClient;
	readonly wrapper: ({ children }: { children: ReactNode }) => ReactNode;
} {
	const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
	return {
		queryClient,
		wrapper: ({ children }) => <QueryClientProvider client={queryClient}>{children}</QueryClientProvider>,
	};
}

function emit(value: DevelopmentAttemptLiveUpdate): void {
	act(() => hubMock.handlers.get("developmentAttemptUpdate")?.(value));
}

describe("useDevelopmentAttemptHub", () => {
	beforeEach(() => {
		hubMock.handlers.clear();
		hubMock.connection.state = "Connected";
		hubMock.connection.on.mockClear();
		hubMock.connection.off.mockClear();
		hubMock.connection.invoke.mockReset();
		hubMock.handle.release.mockClear();
		hubMock.reconnect = undefined;
		hubMock.handle.onReconnected.mockReset();
		hubMock.handle.onReconnected.mockImplementation((callback: () => void) => {
			hubMock.reconnect = callback;
			return vi.fn();
		});
	});

	it("merges the subscription snapshot with buffered updates above its watermark in sequence order", async () => {
		const pending = deferred<DevelopmentAttemptSubscriptionSnapshot>();
		hubMock.connection.invoke.mockReturnValue(pending.promise);
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevelopmentAttemptHub(projectId, taskId, attemptId), { wrapper });
		await waitFor(() => expect(hubMock.connection.invoke).toHaveBeenCalledTimes(1));

		emit(update(7));
		emit(update(4));
		emit(update(6));
		act(() => pending.resolve(snapshot(5, update(5))));

		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		expect(result.current.updates.map((item) => item.sequence)).toEqual([5, 6, 7]);
		expect(result.current.watermark).toBe(7);
		expect(result.current.droppedOrCoalescedUpdateCount).toBe(3);
	});

	it("ignores duplicate and cross-attempt updates after the snapshot", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot(2, update(2)));
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevelopmentAttemptHub(projectId, taskId, attemptId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));

		emit(update(3));
		emit(update(3));
		emit(update(4, { attemptId: "another-attempt" }));

		expect(result.current.updates.map((item) => item.sequence)).toEqual([2, 3]);
		expect(result.current.watermark).toBe(3);
	});

	it("resubscribes after reconnect without regressing the existing watermark", async () => {
		hubMock.connection.invoke.mockResolvedValueOnce(snapshot(8, update(8))).mockResolvedValueOnce(snapshot(5, update(5)));
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevelopmentAttemptHub(projectId, taskId, attemptId), { wrapper });
		await waitFor(() => expect(result.current.watermark).toBe(8));

		act(() => hubMock.reconnect?.());

		await waitFor(() => expect(hubMock.connection.invoke).toHaveBeenCalledTimes(2));
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		expect(result.current.watermark).toBe(8);
		expect(result.current.updates.map((item) => item.sequence)).toEqual([8]);
	});

	it("invalidates durable project state once for a deduplicated terminal update", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot(1));
		const { queryClient, wrapper } = harness();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();
		const { result } = renderHook(() => useDevelopmentAttemptHub(projectId, taskId, attemptId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const terminal = update(2, { kind: "Terminal", status: "Succeeded" });

		emit(terminal);
		emit(terminal);

		expect(invalidate).toHaveBeenCalledTimes(1);
		expect(invalidate).toHaveBeenCalledWith({
			queryKey: developmentInvalidationKey(developmentQueryIds.getProject),
		});
	});
});

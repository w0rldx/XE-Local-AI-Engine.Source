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

import { type DevelopmentLivePhase, useDevelopmentAttemptHub } from "@/features/development/hooks/useDevelopmentAttemptHub";

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

	it("folds command output per execution and keeps it out of the update history", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot(1));
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevelopmentAttemptHub(projectId, taskId, attemptId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		const command = { currentCommandId: "test", currentCommandExecutionId: "exec-1" };

		emit(update(2, { kind: "Command", currentOperationElapsedMilliseconds: 0, outputDelta: null, ...command }));
		emit(update(3, { kind: "CommandOutput", outputDelta: "one\ntwo\n", ...command }));
		emit(update(4, { kind: "CommandOutput", outputDelta: "three\n", commandOutputSkippedLines: 5, ...command }));

		expect(result.current.updates.map((item) => item.sequence)).toEqual([2]);
		expect(result.current.commands).toHaveLength(1);
		expect(result.current.commands[0]?.output.lines.map((line) => line.text)).toEqual(["one", "two", "three"]);
		expect(result.current.commands[0]?.skippedLines).toBe(5);
		expect(result.current.commands[0]?.running).toBe(true);

		// The backend's completion carries the skipped count (0 here) and the command's duration (0 ms for a fast one).
		emit(
			update(5, {
				kind: "Command",
				currentOperationElapsedMilliseconds: 0,
				commandOutputSkippedLines: 0,
				outputDelta: null,
				...command,
			}),
		);

		expect(result.current.commands[0]?.running).toBe(false);
		expect(result.current.commands[0]?.output.lines).toHaveLength(3);
	});

	it("drops a completion whose start it never saw, even a 0 ms one, instead of showing it as running", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot(1));
		const { wrapper } = harness();
		const { result } = renderHook(() => useDevelopmentAttemptHub(projectId, taskId, attemptId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));

		emit(
			update(2, {
				kind: "Command",
				currentCommandId: "build",
				currentCommandExecutionId: "exec-missed",
				currentOperationElapsedMilliseconds: 0,
				commandOutputSkippedLines: 0,
				outputDelta: null,
			}),
		);

		expect(result.current.commands).toEqual([]);
	});

	it("ends every running command on Terminal and does not resubscribe that stream on a later poll of the same phase", async () => {
		hubMock.connection.invoke
			.mockResolvedValueOnce(snapshot(1))
			.mockRejectedValueOnce(new Error("The Development attempt has no active live stream."));
		const { wrapper } = harness();
		const { result, rerender } = renderHook(
			({ token }: { token: number }) => useDevelopmentAttemptHub(projectId, taskId, attemptId, "validation", token),
			{ wrapper, initialProps: { token: 1 } },
		);
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));

		emit(update(2, { kind: "CommandOutput", outputDelta: "x\n", currentCommandExecutionId: "exec-1" }));
		emit(update(3, { kind: "Terminal", status: "Succeeded", currentActivity: "Validation passed." }));
		expect(result.current.commands[0]?.running).toBe(false);
		expect(result.current.latest?.currentActivity).toBe("Validation passed.");

		// A reconnect after the end is refused; the next poll in the same phase must leave the finished stream alone.
		act(() => hubMock.reconnect?.());
		await waitFor(() => expect(result.current.connectionState).toBe("unavailable"));
		rerender({ token: 2 });

		expect(hubMock.connection.invoke).toHaveBeenCalledTimes(2);
	});

	it("subscribes afresh when validation starts under the coder attempt whose own run just ended", async () => {
		// Sequences are node-wide: validation's stream starts above the coder run's last number, here 9.
		const validationSnapshot = deferred<DevelopmentAttemptSubscriptionSnapshot>();
		hubMock.connection.invoke.mockResolvedValueOnce(snapshot(1)).mockReturnValueOnce(validationSnapshot.promise);
		const { queryClient, wrapper } = harness();
		const initialProps: { phase: DevelopmentLivePhase; token: number } = { phase: "attempt", token: 1 };
		const { result, rerender } = renderHook(
			({ phase, token }) => useDevelopmentAttemptHub(projectId, taskId, attemptId, phase, token),
			{ wrapper, initialProps },
		);
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		emit(update(5, { kind: "CommandOutput", outputDelta: "coder\n", currentCommandExecutionId: "exec-coder" }));

		// Same attempt id, a new stream: the poll that sees Validation flips the phase.
		rerender({ phase: "validation", token: 2 });
		await waitFor(() => expect(hubMock.connection.invoke).toHaveBeenCalledTimes(2));
		const invalidate = vi.spyOn(queryClient, "invalidateQueries");

		// The coder run's delivery loop still drains into the same hub group: its Terminal lands before the snapshot, a
		// late output after it. Both are at or below the snapshot watermark and must not end or pollute validation.
		emit(update(9, { kind: "Terminal", status: "Succeeded" }));
		act(() => validationSnapshot.resolve(snapshot(9)));
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		emit(update(8, { kind: "CommandOutput", outputDelta: "late coder\n", currentCommandExecutionId: "exec-coder" }));
		expect(result.current.commands).toEqual([]);
		expect(result.current.latest).toBeNull();
		expect(invalidate).not.toHaveBeenCalled();

		emit(
			update(10, {
				kind: "Command",
				currentCommandId: "test",
				currentCommandExecutionId: "exec-validation",
				currentOperationElapsedMilliseconds: 0,
				outputDelta: null,
			}),
		);
		emit(update(11, { kind: "CommandOutput", outputDelta: "Passed!\n", currentCommandExecutionId: "exec-validation" }));

		expect(result.current.watermark).toBe(11);
		expect(result.current.commands.map((command) => [command.executionId, command.running])).toEqual([["exec-validation", true]]);
		expect(result.current.commands[0]?.output.lines.map((line) => line.text)).toEqual(["Passed!"]);
	});

	it("retries a refused subscription when the retry token changes", async () => {
		hubMock.connection.invoke.mockRejectedValueOnce(new Error("no active live stream")).mockResolvedValueOnce(snapshot(4));
		const { wrapper } = harness();
		const { result, rerender } = renderHook(
			({ token }: { token: number }) => useDevelopmentAttemptHub(projectId, taskId, attemptId, "attempt", token),
			{ wrapper, initialProps: { token: 1 } },
		);
		await waitFor(() => expect(result.current.connectionState).toBe("unavailable"));

		rerender({ token: 1 });
		expect(hubMock.connection.invoke).toHaveBeenCalledTimes(1);

		rerender({ token: 2 });

		await waitFor(() => expect(result.current.connectionState).toBe("connected"));
		expect(hubMock.connection.invoke).toHaveBeenCalledTimes(2);
		expect(result.current.watermark).toBe(4);
	});

	// The WIRE-CONTRACT pin. This hook has no zod schema, so a numeric `kind` is not rejected — it simply matches no
	// branch, which is why the terminal invalidation was silently dead. Both wire forms go in as literal JSON text.
	it("invalidates on a terminal frame whose kind is the enum name and not on one carrying a raw enum number", async () => {
		hubMock.connection.invoke.mockResolvedValue(snapshot(1));
		const { queryClient, wrapper } = harness();
		const invalidate = vi.spyOn(queryClient, "invalidateQueries").mockResolvedValue();
		const { result } = renderHook(() => useDevelopmentAttemptHub(projectId, taskId, attemptId), { wrapper });
		await waitFor(() => expect(result.current.connectionState).toBe("connected"));

		// DevelopmentAttemptLiveUpdateKind.Terminal / DevelopmentAttemptStatus.Succeeded as their ordinals.
		emitWire(terminalWire(2, "7", "2"));
		expect(invalidate).not.toHaveBeenCalled();
		expect(result.current.latest?.kind).not.toBe("Terminal");

		emitWire(terminalWire(3, '"Terminal"', '"Succeeded"'));

		expect(invalidate).toHaveBeenCalledTimes(1);
		expect(result.current.latest?.kind).toBe("Terminal");
		expect(result.current.latest?.status).toBe("Succeeded");
	});
});

// A server frame verbatim: camelCase members, every nullable present, `kind` and `status` substituted so the same text
// serves both the pre-fix ordinal shape and the name the hub emits now.
function terminalWire(sequence: number, kind: string, status: string): string {
	return `{"projectId":"${projectId}","taskId":"${taskId}","attemptId":"${attemptId}","sequence":${sequence},"occurredAtUtc":${sequence},"kind":${kind},"role":"Coder","status":${status},"modelId":"coder-model","provider":"local","outputDelta":null,"currentActivity":null,"inputTokens":null,"outputTokens":null,"reasoningTokens":null,"outputTokensPerSecond":null,"providerRoundCount":1,"toolCallCount":0,"commandCount":0,"currentToolId":null,"currentCommandId":null,"currentOperationElapsedMilliseconds":null,"changedFileCount":0,"patchByteCount":0,"subjectHash":null,"contextUsagePercent":null,"contextHeadroomPercent":null,"secondsSinceMeaningfulProgress":0,"warningCategory":null,"warningMessage":null}`;
}

function emitWire(wireText: string): void {
	act(() => hubMock.handlers.get("developmentAttemptUpdate")?.(JSON.parse(wireText) as DevelopmentAttemptLiveUpdate));
}

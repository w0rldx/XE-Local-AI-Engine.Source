// @vitest-environment jsdom

import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { resetSharedHubConnectionsForTest } from "@/core/api/signalr/SharedHubConnection";
import { benchmarkHubEvents, useBenchmarkRunHub } from "@/features/benchmarks/hooks/useBenchmarkRunHub";
import type { BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";
import { benchmarkRunDetailFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";

const handlers = new Map<string, (payload: unknown) => void>();
const invoke = vi.fn(() => Promise.resolve());
let onReconnected: (() => void) | undefined;
let state = "Disconnected";

vi.mock("@microsoft/signalr", () => {
	class FakeBuilder {
		withUrl() {
			return this;
		}
		withAutomaticReconnect() {
			return this;
		}
		configureLogging() {
			return this;
		}
		build() {
			return {
				get state() {
					return state;
				},
				on: (name: string, handler: (payload: unknown) => void) => handlers.set(name, handler),
				off: (name: string) => handlers.delete(name),
				onreconnected: (callback: () => void) => {
					onReconnected = callback;
				},
				start: () => {
					state = "Connected";
					return Promise.resolve();
				},
				stop: () => Promise.resolve(),
				invoke,
			};
		}
	}
	return {
		HubConnectionBuilder: FakeBuilder,
		HubConnectionState: { Connected: "Connected", Disconnected: "Disconnected" },
		LogLevel: { Warning: 3 },
	};
});
vi.mock("@/core/auth/stores/NodeAuthStore", () => ({ useNodeAuthStore: { getState: () => ({ accessToken: "token" }) } }));

function run(overrides: Partial<BenchmarkRunDetail> = {}): BenchmarkRunDetail {
	return benchmarkRunDetailFixture({
		primaryModelOrigin: "imported",
		primaryStatus: "Running",
		effectiveContextTokens: null,
		durationMs: null,
		totalTokens: null,
		tokensPerSecond: null,
		lastStreamSequence: 0,
		version: 1,
		updatedAtUtc: 1,
		primaryCompletedAtUtc: null,
		...overrides,
	});
}

describe("useBenchmarkRunHub", () => {
	beforeEach(() => {
		resetSharedHubConnectionsForTest();
		handlers.clear();
		invoke.mockClear();
		onReconnected = undefined;
		state = "Disconnected";
	});

	it("deduplicates events and resubscribes from the contiguous cursor", async () => {
		const refetch = vi.fn(async () => run());
		const { result, unmount } = renderHook(() => useBenchmarkRunHub({ run: run(), refetch }));
		await waitFor(() => expect(invoke).toHaveBeenCalledWith("Subscribe", "run-1", 0));
		act(() => {
			handlers.get(benchmarkHubEvents.event)?.({ runId: "run-1", sequence: 1, kind: "OutputDelta", payload: { content: "one" } });
			handlers.get(benchmarkHubEvents.event)?.({
				runId: "run-1",
				sequence: 1,
				kind: "OutputDelta",
				payload: { content: "duplicate" },
			});
		});
		expect(result.current.parts).toEqual([{ kind: "output", content: "one" }]);
		invoke.mockClear();
		act(() => onReconnected?.());
		await waitFor(() => expect(invoke).toHaveBeenCalledWith("Subscribe", "run-1", 1));
		unmount();
		expect(invoke).toHaveBeenCalledWith("Unsubscribe", "run-1");
	});

	it("does not let an active HTTP refresh erase newer live output or regress the cursor", async () => {
		const refetch = vi.fn(async () => run());
		const initial = run();
		const { result, rerender } = renderHook(
			({ snapshot }: { snapshot: BenchmarkRunDetail }) => useBenchmarkRunHub({ run: snapshot, refetch }),
			{ initialProps: { snapshot: initial } },
		);
		await waitFor(() => expect(invoke).toHaveBeenCalledWith("Subscribe", "run-1", 0));

		act(() => {
			handlers.get(benchmarkHubEvents.event)?.({ runId: "run-1", sequence: 1, kind: "OutputDelta", payload: { content: "one" } });
		});
		rerender({ snapshot: run({ updatedAtUtc: 2, lastStreamSequence: 0, outputParts: [] }) });
		act(() => {
			handlers.get(benchmarkHubEvents.event)?.({ runId: "run-1", sequence: 2, kind: "OutputDelta", payload: { content: "two" } });
		});

		expect(result.current.lastSequence).toBe(2);
		expect(result.current.parts).toEqual([{ kind: "output", content: "onetwo" }]);
		expect(refetch).not.toHaveBeenCalled();
	});

	it("refetches on a sequence gap, replay reset, and terminal snapshot", async () => {
		const durable = run({ lastStreamSequence: 4, outputParts: [{ kind: "output", content: "durable" }] });
		const refetch = vi.fn(async () => durable);
		const { result } = renderHook(() => useBenchmarkRunHub({ run: run(), refetch }));
		await waitFor(() => expect(invoke).toHaveBeenCalled());
		act(() =>
			handlers.get(benchmarkHubEvents.event)?.({ runId: "run-1", sequence: 3, kind: "OutputDelta", payload: { content: "gap" } }),
		);
		await waitFor(() => expect(refetch).toHaveBeenCalledTimes(1));
		await waitFor(() => expect(result.current.parts).toEqual([{ kind: "output", content: "durable" }]));
		act(() => handlers.get(benchmarkHubEvents.replayReset)?.({ runId: "run-1", latestSequence: 5, runVersion: 2 }));
		await waitFor(() => expect(refetch).toHaveBeenCalledTimes(2));
		act(() =>
			handlers.get(benchmarkHubEvents.event)?.({
				runId: "run-1",
				sequence: 5,
				kind: "TerminalSnapshotAvailable",
				payload: { state: "Succeeded" },
			}),
		);
		await waitFor(() => expect(refetch).toHaveBeenCalledTimes(3));
	});
	// JudgeState and Metrics carry no output, so they never touch `parts`; they correct what the pane renders until the
	// next authoritative read, which is why they live in an overlay instead of in the query cache.
	it("keeps judge-state and metric events as an overlay", async () => {
		const refetch = vi.fn(async () => run());
		const { result } = renderHook(() => useBenchmarkRunHub({ run: run(), refetch }));
		await waitFor(() => expect(invoke).toHaveBeenCalledWith("Subscribe", "run-1", 0));

		act(() => {
			handlers.get(benchmarkHubEvents.event)?.({ runId: "run-1", sequence: 1, kind: "JudgeState", payload: { state: "running" } });
			handlers.get(benchmarkHubEvents.event)?.({
				runId: "run-1",
				sequence: 2,
				kind: "Metrics",
				payload: {
					effectiveContextTokens: 4096,
					durationMs: 1200,
					totalTokens: 30,
					tokensPerSecond: 25,
					ttftMs: 180.25,
					promptTokens: 123,
					promptTokensPerSecond: 269.4,
					generationTokens: 89,
					generationTokensPerSecond: 88,
					cachedPromptTokens: 7,
					segmentCount: 2,
				},
			});
		});

		// The live Metrics event carries the pp/tg split too, so the pane shows the breakdown while the run is still
		// streaming rather than only after the durable snapshot is re-read.
		expect(result.current.overlay).toEqual({
			judgeState: "running",
			effectiveContextTokens: 4096,
			durationMs: 1200,
			totalTokens: 30,
			tokensPerSecond: 25,
			throughput: {
				ttftMs: 180.25,
				promptTokens: 123,
				promptTokensPerSecond: 269.4,
				generationTokens: 89,
				generationTokensPerSecond: 88,
				cachedPromptTokens: 7,
				segmentCount: 2,
			},
		});
		expect(result.current.parts).toEqual([]);
		expect(result.current.lastSequence).toBe(2);
	});

	// The durable snapshot is the authority: once it is re-read, the streamed corrections are spent.
	it("drops the overlay when the durable snapshot is re-read", async () => {
		const refetch = vi.fn(async () => run({ lastStreamSequence: 5 }));
		const { result } = renderHook(() => useBenchmarkRunHub({ run: run(), refetch }));
		await waitFor(() => expect(invoke).toHaveBeenCalled());
		act(() =>
			handlers.get(benchmarkHubEvents.event)?.({ runId: "run-1", sequence: 1, kind: "JudgeState", payload: { state: "running" } }),
		);
		expect(result.current.overlay.judgeState).toBe("running");

		act(() => handlers.get(benchmarkHubEvents.replayReset)?.({ runId: "run-1", latestSequence: 5, runVersion: 2 }));

		await waitFor(() => expect(result.current.overlay.judgeState).toBeNull());
	});
});

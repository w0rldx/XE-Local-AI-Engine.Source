// @vitest-environment jsdom

import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { resetSharedHubConnectionsForTest } from "@/core/api/signalr/SharedHubConnection";
import { benchmarkHubEvents, useBenchmarkRunHub } from "@/features/benchmarks/hooks/useBenchmarkRunHub";
import type { BenchmarkRunDetail } from "@/features/benchmarks/models/BenchmarkModels";

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
	return {
		id: "run-1",
		projectId: "project-1",
		primaryModelName: "model",
		primaryModelOrigin: "imported",
		modelContentFingerprint: "v1:abc",
		agentName: "agent",
		agentVersion: 1,
		requestedContextTokens: 4096,
		primaryStatus: "Running",
		judgeStatus: "Pending",
		effectiveContextTokens: null,
		durationMs: null,
		totalTokens: null,
		tokensPerSecond: null,
		userScore: null,
		lastStreamSequence: 0,
		version: 1,
		createdAtUtc: 1,
		updatedAtUtc: 1,
		outputParts: [],
		judgeResult: null,
		primaryErrorMessage: null,
		judgeErrorMessage: null,
		startedAtUtc: 1,
		primaryCompletedAtUtc: null,
		judgeStartedAtUtc: null,
		judgeCompletedAtUtc: null,
		...overrides,
	};
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

	it("refetches on a sequence gap, replay reset, and terminal snapshot", async () => {
		const durable = run({ lastStreamSequence: 4, outputParts: [{ kind: "output", content: "durable" }] });
		const refetch = vi.fn(async () => durable);
		const { result } = renderHook(() => useBenchmarkRunHub({ run: run(), refetch }));
		await waitFor(() => expect(invoke).toHaveBeenCalled());
		act(() =>
			handlers.get(benchmarkHubEvents.event)?.({ runId: "run-1", sequence: 3, kind: "OutputDelta", payload: { content: "gap" } }),
		);
		await waitFor(() => expect(refetch).toHaveBeenCalledTimes(1));
		expect(result.current.parts).toEqual([{ kind: "output", content: "durable" }]);
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
});

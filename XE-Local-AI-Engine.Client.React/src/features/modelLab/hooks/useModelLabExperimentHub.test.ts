// @vitest-environment jsdom

import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import { useModelLabExperimentHub } from "@/features/modelLab/hooks/useModelLabExperimentHub";
import { activeJobPollMs } from "@/features/modelLab/models/ModelLabTrace";

const hub = vi.hoisted(() => ({
	whenStarted: Promise.resolve(),
	state: "Connected",
	invoke: vi.fn(),
	reconnecting: [] as (() => void)[],
	reconnected: [] as (() => void)[],
	closed: [] as (() => void)[],
	handlers: new Map<string, (value: unknown) => void>(),
}));

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: () => ({
		connection: {
			get state() {
				return hub.state;
			},
			on: (name: string, handler: (value: unknown) => void) => hub.handlers.set(name, handler),
			off: vi.fn(),
			invoke: hub.invoke,
		},
		whenStarted: hub.whenStarted,
		onReconnecting: (callback: () => void) => hub.reconnecting.push(callback),
		onReconnected: (callback: () => void) => {
			hub.reconnected.push(callback);
			return () => undefined;
		},
		onClosed: (callback: () => void) => hub.closed.push(callback),
		release: vi.fn(),
	}),
}));

const experimentId = "11111111-1111-4111-8111-111111111111";

describe("useModelLabExperimentHub", () => {
	beforeEach(() => {
		hub.whenStarted = Promise.resolve();
		hub.state = "Connected";
		hub.invoke.mockReset();
		hub.invoke.mockResolvedValue(undefined);
		hub.reconnecting.length = 0;
		hub.reconnected.length = 0;
		hub.closed.length = 0;
	});

	it("reads the row again when a phase starts, because a finished arm was saved just before it", async () => {
		const onResync = vi.fn();
		const { result } = renderHook(() => useModelLabExperimentHub(experimentId, onResync));
		await waitFor(() => expect(result.current.isLive).toBe(true));
		onResync.mockClear();

		act(() =>
			hub.handlers.get("trainingRun.event")?.({
				runId: experimentId,
				sequence: 1,
				kind: "Progress",
				payload: { step: 1, totalSteps: 7 },
			}),
		);
		expect(onResync).not.toHaveBeenCalled();

		act(() =>
			hub.handlers.get("trainingRun.event")?.({
				runId: experimentId,
				sequence: 2,
				kind: "Phase",
				payload: { phase: "arm-vector" },
			}),
		);
		expect(onResync).toHaveBeenCalledTimes(1);
	});

	it("drops a phase's count when the next phase starts", async () => {
		const { result } = renderHook(() => useModelLabExperimentHub(experimentId, vi.fn()));
		await waitFor(() => expect(result.current.isLive).toBe(true));
		const send = (sequence: number, kind: string, payload: unknown) =>
			act(() => hub.handlers.get("trainingRun.event")?.({ runId: experimentId, sequence, kind, payload }));

		send(1, "Progress", { step: 16, totalSteps: 16 });
		expect(result.current.totalSteps).toBe(16);

		send(2, "Phase", { phase: "arm-baseline" });

		expect(result.current.phase).toBe("arm-baseline");
		expect(result.current.totalSteps).toBeNull();
		expect(result.current.step).toBeNull();
	});

	it("folds a frame past a gap and reads the row, because the row holds what the gap carried", async () => {
		const onResync = vi.fn();
		const { result } = renderHook(() => useModelLabExperimentHub(experimentId, onResync));
		await waitFor(() => expect(result.current.isLive).toBe(true));

		act(() =>
			hub.handlers.get("trainingRun.event")?.({
				runId: experimentId,
				sequence: 5,
				kind: "Progress",
				payload: { step: 3, totalSteps: 7 },
			}),
		);

		expect(result.current.step).toBe(3);
		expect(onResync).toHaveBeenCalledTimes(1);
	});

	it("drops a frame whose kind the server's enum does not declare", async () => {
		const onResync = vi.fn();
		const { result } = renderHook(() => useModelLabExperimentHub(experimentId, onResync));
		await waitFor(() => expect(result.current.isLive).toBe(true));

		act(() => hub.handlers.get("trainingRun.event")?.({ runId: experimentId, sequence: 1, kind: 2, payload: { step: 3 } }));

		expect(result.current.step).toBeNull();
		expect(onResync).not.toHaveBeenCalled();
	});

	it("takes a replay reset's sequence as the cursor and reads the row", async () => {
		const onResync = vi.fn();
		const { result } = renderHook(() => useModelLabExperimentHub(experimentId, onResync));
		await waitFor(() => expect(result.current.isLive).toBe(true));

		act(() => hub.handlers.get("trainingRun.replayReset")?.({ runId: experimentId, latestSequence: 9, runVersion: 1 }));
		expect(onResync).toHaveBeenCalledTimes(1);

		act(() =>
			hub.handlers.get("trainingRun.event")?.({
				runId: experimentId,
				sequence: 9,
				kind: "Progress",
				payload: { step: 3 },
			}),
		);
		expect(result.current.step).toBeNull();
	});

	it("is live only once the hub accepted the subscription, and not while it reconnects", async () => {
		const { result } = renderHook(() => useModelLabExperimentHub(experimentId, vi.fn()));

		expect(result.current.isLive).toBe(false);
		await waitFor(() => expect(result.current.isLive).toBe(true));

		act(() => hub.reconnecting[0]?.());
		expect(result.current.isLive).toBe(false);

		act(() => hub.reconnected[0]?.());
		await waitFor(() => expect(result.current.isLive).toBe(true));

		act(() => hub.closed[0]?.());
		expect(result.current.isLive).toBe(false);
	});

	it("stays on the poll when the hub never starts, without an unhandled rejection", async () => {
		const started = Promise.reject(new Error("negotiation failed"));
		const settled = started.catch(() => undefined);
		hub.whenStarted = started;

		const { result } = renderHook(() => useModelLabExperimentHub(experimentId, vi.fn()));
		await act(() => settled);

		expect(result.current.isLive).toBe(false);
		expect(hub.invoke).not.toHaveBeenCalled();
	});

	it("stays at the fallback rate when the start settles with the connection still down, as the real manager's does", async () => {
		hub.state = "Disconnected";

		const { result } = renderHook(() => useModelLabExperimentHub(experimentId, vi.fn()));
		await act(() => hub.whenStarted);

		expect(result.current.isLive).toBe(false);
		expect(activeJobPollMs(result.current.isLive)).toBe(5_000);
		expect(hub.invoke).not.toHaveBeenCalled();
	});

	it("stays on the poll and reads the row when the subscription is refused", async () => {
		hub.invoke.mockRejectedValue(new Error("refused"));
		const onResync = vi.fn();

		const { result } = renderHook(() => useModelLabExperimentHub(experimentId, onResync));

		await waitFor(() => expect(onResync).toHaveBeenCalledTimes(1));
		expect(result.current.isLive).toBe(false);
	});

	it("is not live with no job to follow", () => {
		const { result } = renderHook(() => useModelLabExperimentHub(null, vi.fn()));

		expect(result.current.isLive).toBe(false);
	});
});

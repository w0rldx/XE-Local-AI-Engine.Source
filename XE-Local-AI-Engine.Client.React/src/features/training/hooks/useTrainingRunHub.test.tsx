// @vitest-environment jsdom

// The WIRE-CONTRACT guard for `trainingRun.event`. Every frame here is the literal JSON text the hub emits, parsed with
// JSON.parse rather than written as a typed object, so the `kind` spelling and the all-members-present shape
// (DefaultIgnoreCondition.Never sends every nullable as null) are the thing under test, not a fixture's opinion of them.

import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

const hubMock = vi.hoisted(() => {
	const handlers = new Map<string, (frame: unknown) => void>();
	const connection = {
		state: "Connected",
		on: vi.fn((event: string, handler: (frame: unknown) => void) => handlers.set(event, handler)),
		off: vi.fn((event: string) => handlers.delete(event)),
		invoke: vi.fn(async (): Promise<unknown> => undefined),
	};
	const handle = { connection, whenStarted: Promise.resolve(), onReconnected: vi.fn(), release: vi.fn() };
	return { acquire: vi.fn(() => handle), connection, handlers };
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: hubMock.acquire,
}));

import { useTrainingRunHub } from "@/features/training/hooks/useTrainingRunHub";

const runId = "11111111-1111-4111-8111-111111111111";

// One real server frame, verbatim. `kind` is the enum's declared NAME and every nullable payload member is present.
const progressFrame = `{"runId":"${runId}","sequence":1,"kind":"Progress","payload":{"state":null,"phase":"training","step":7,"totalSteps":40,"epoch":1.5,"loss":1.25,"learningRate":0.0002,"vramBytes":null,"message":null,"runVersion":3}}`;
// The same frame as it left the server BEFORE this slice: the enum as its ordinal.
const numericKindFrame = `{"runId":"${runId}","sequence":1,"kind":2,"payload":{"state":null,"phase":"training","step":7,"totalSteps":40,"epoch":1.5,"loss":1.25,"learningRate":0.0002,"vramBytes":null,"message":null,"runVersion":3}}`;
// An evaluation riding the run's own group: `state` is the EVALUATION's status and step/totalSteps are samples scored.
const evaluationStateFrame = `{"runId":"${runId}","sequence":2,"kind":"EvaluationState","payload":{"state":"Succeeded","phase":null,"step":12,"totalSteps":12,"epoch":null,"loss":null,"learningRate":null,"vramBytes":null,"message":null,"runVersion":null,"evaluationId":"22222222-2222-4222-8222-222222222222","passedCount":9}}`;

function emit(wireText: string): void {
	const handler = hubMock.handlers.get("trainingRun.event");
	if (!handler) {
		throw new Error("the hook did not subscribe to trainingRun.event");
	}
	act(() => handler(JSON.parse(wireText)));
}

describe("useTrainingRunHub", () => {
	beforeEach(() => {
		hubMock.handlers.clear();
	});

	it("folds a progress frame whose kind is the enum name", async () => {
		const resync = vi.fn();
		const { result } = renderHook(() => useTrainingRunHub(runId, resync));
		await waitFor(() => expect(hubMock.connection.invoke).toHaveBeenCalledWith("Subscribe", runId, 0));

		emit(progressFrame);

		expect(result.current.step).toBe(7);
		expect(result.current.totalSteps).toBe(40);
		expect(result.current.loss).toBe(1.25);
		expect(result.current.phase).toBe("training");
	});

	it("drops a frame whose kind is a raw enum number", async () => {
		const resync = vi.fn();
		const { result } = renderHook(() => useTrainingRunHub(runId, resync));
		await waitFor(() => expect(hubMock.connection.invoke).toHaveBeenCalledWith("Subscribe", runId, 0));

		emit(numericKindFrame);

		expect(result.current.step).toBe(0);
		expect(result.current.totalSteps).toBe(0);
		expect(result.current.phase).toBeNull();
		expect(resync).not.toHaveBeenCalled();
	});

	// EvaluationState and EvaluationProgress were missing from the client's kind list, so these frames failed the parse
	// and were dropped: the evaluations list only ever refreshed on its own poll.
	it("resyncs on an evaluation status frame without moving the run's own counters", async () => {
		const resync = vi.fn();
		const { result } = renderHook(() => useTrainingRunHub(runId, resync));
		await waitFor(() => expect(hubMock.connection.invoke).toHaveBeenCalledWith("Subscribe", runId, 0));

		emit(progressFrame);
		expect(resync).not.toHaveBeenCalled();
		emit(evaluationStateFrame);

		expect(resync).toHaveBeenCalledTimes(1);
		// The evaluation's 12/12 and its "Succeeded" must not become the run's progress or the run's status.
		expect(result.current.step).toBe(7);
		expect(result.current.totalSteps).toBe(40);
		expect(result.current.status).toBeNull();
	});
});

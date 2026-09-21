// @vitest-environment jsdom

// The WIRE-CONTRACT guard for `datasetGeneration.event`. Frames are the literal JSON text the hub emits, parsed with
// JSON.parse rather than written as typed objects, so the `kind` spelling is what is being asserted.

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

import { useDatasetGenerationHub } from "@/features/training/hooks/useDatasetGenerationHub";

const datasetId = "33333333-3333-4333-8333-333333333333";

const progressFrame = `{"datasetId":"${datasetId}","sequence":1,"kind":"Progress","payload":{"state":null,"completed":3,"total":10,"kind":null,"label":null,"reason":null,"datasetVersion":null}}`;
const rejectedFrame = `{"datasetId":"${datasetId}","sequence":2,"kind":"Rejected","payload":{"state":null,"completed":3,"total":10,"kind":"single-tool-call","label":null,"reason":"schema","datasetVersion":null}}`;
// The pre-fix shape: DatasetGenerationEventKind.Progress as its ordinal.
const numericKindFrame = `{"datasetId":"${datasetId}","sequence":1,"kind":1,"payload":{"state":null,"completed":3,"total":10,"kind":null,"label":null,"reason":null,"datasetVersion":null}}`;

function emit(wireText: string): void {
	const handler = hubMock.handlers.get("datasetGeneration.event");
	if (!handler) {
		throw new Error("the hook did not subscribe to datasetGeneration.event");
	}
	act(() => handler(JSON.parse(wireText)));
}

describe("useDatasetGenerationHub", () => {
	beforeEach(() => {
		hubMock.handlers.clear();
	});

	it("counts a generation frame whose kind is the enum name", async () => {
		const resync = vi.fn();
		const { result } = renderHook(() => useDatasetGenerationHub(datasetId, resync));
		await waitFor(() => expect(hubMock.connection.invoke).toHaveBeenCalledWith("Subscribe", datasetId, 0));

		emit(progressFrame);
		emit(rejectedFrame);

		expect(result.current.completed).toBe(3);
		expect(result.current.total).toBe(10);
		expect(result.current.rejected).toBe(1);
	});

	it("drops a frame whose kind is a raw enum number", async () => {
		const resync = vi.fn();
		const { result } = renderHook(() => useDatasetGenerationHub(datasetId, resync));
		await waitFor(() => expect(hubMock.connection.invoke).toHaveBeenCalledWith("Subscribe", datasetId, 0));

		emit(numericKindFrame);

		expect(result.current.completed).toBe(0);
		expect(result.current.total).toBe(0);
		expect(resync).not.toHaveBeenCalled();
	});
});

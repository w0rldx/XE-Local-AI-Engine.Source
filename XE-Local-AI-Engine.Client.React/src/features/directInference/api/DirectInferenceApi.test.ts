// The stream call against a faked shared-hub seam whose connection start the test controls.

import { afterEach, describe, expect, it, vi } from "vitest";

import { streamDirectInference } from "@/features/directInference/api/DirectInferenceApi";

const hub = vi.hoisted(() => {
	let start: () => void = () => undefined;
	const connection = { state: "Connected", stream: vi.fn() };
	const handle = {
		connection,
		whenStarted: Promise.resolve(),
		release: vi.fn(),
	};
	return {
		connection,
		handle,
		pendingStart(): void {
			handle.whenStarted = new Promise<void>((resolve) => {
				start = resolve;
			});
		},
		finishStart(): void {
			start();
		},
	};
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: () => hub.handle,
}));

const request = { modelId: "qwen3", prompt: "Hi" };

describe("streamDirectInference", () => {
	afterEach(() => {
		hub.connection.stream.mockReset();
		hub.handle.release.mockClear();
	});

	it("sends no stream when the request is aborted while the hub is still connecting", async () => {
		hub.pendingStart();
		const controller = new AbortController();

		const streaming = streamDirectInference(request, vi.fn(), controller.signal);
		controller.abort();
		hub.finishStart();
		await streaming;

		expect(hub.connection.stream).not.toHaveBeenCalled();
		expect(hub.handle.release).toHaveBeenCalledTimes(1);
	});

	it("sends no stream and takes no hub when the signal is already aborted", async () => {
		const controller = new AbortController();
		controller.abort();

		await streamDirectInference(request, vi.fn(), controller.signal);

		expect(hub.connection.stream).not.toHaveBeenCalled();
		expect(hub.handle.release).not.toHaveBeenCalled();
	});

	it("disposes the stream on an abort after it started", async () => {
		const dispose = vi.fn();
		hub.connection.stream.mockReturnValue({ subscribe: () => ({ dispose }) });
		const controller = new AbortController();

		const streaming = streamDirectInference(request, vi.fn(), controller.signal);
		await vi.waitFor(() => expect(hub.connection.stream).toHaveBeenCalledWith("StreamDirectInference", request));
		controller.abort();
		await streaming;

		expect(dispose).toHaveBeenCalledTimes(1);
	});
});

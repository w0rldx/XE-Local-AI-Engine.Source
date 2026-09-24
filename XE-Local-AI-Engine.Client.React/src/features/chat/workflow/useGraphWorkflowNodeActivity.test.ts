// @vitest-environment jsdom

// The live-activity wire contract on `graph-workflows/hub`: the hub path, `StreamNodeActivity(runId, nodeKey)`, the
// frames folded by the chat's own reducer, re-keying on a new invocation id, a reconnect re-opening the stream, and a
// refused open (HubException) reading as "nothing live" rather than as an error.

import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";

import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";

interface Observer {
	next: (event: NodeChatStreamEventDto) => void;
	error: (error: unknown) => void;
	complete: () => void;
}

const hubMock = vi.hoisted(() => {
	const streams: { args: unknown[]; observer: Observer; dispose: ReturnType<typeof vi.fn> }[] = [];
	const connection = {
		state: "Connected",
		stream: vi.fn((...args: unknown[]) => ({
			subscribe: (observer: Observer) => {
				const dispose = vi.fn();
				streams.push({ args, observer, dispose });
				return { dispose };
			},
		})),
	};
	const handle = {
		connection,
		whenStarted: Promise.resolve(),
		onReconnected: vi.fn(),
		release: vi.fn(),
	};
	return { acquire: vi.fn(() => handle), connection, handle, streams, reconnect: undefined as (() => void) | undefined };
});

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: hubMock.acquire,
}));

import { useGraphWorkflowNodeActivity } from "@/features/chat/workflow/useGraphWorkflowNodeActivity";

const runId = "run-1";
const nodeKey = "draft";

function frame(overrides: Partial<NodeChatStreamEventDto>): NodeChatStreamEventDto {
	return {
		type: "assistant-delta",
		conversationId: "graph-conversation",
		messageId: "invocation-1",
		requestId: "invocation-1",
		status: "streaming",
		sequence: 0,
		occurredAtUtc: 1_000,
		...overrides,
	};
}

function latest() {
	const stream = hubMock.streams.at(-1);
	if (!stream) {
		throw new Error("No stream was opened.");
	}
	return stream;
}

function emit(event: NodeChatStreamEventDto): void {
	act(() => latest().observer.next(event));
}

describe("useGraphWorkflowNodeActivity", () => {
	beforeEach(() => {
		hubMock.streams.length = 0;
		hubMock.connection.state = "Connected";
		hubMock.connection.stream.mockClear();
		hubMock.acquire.mockClear();
		hubMock.handle.release.mockClear();
		hubMock.reconnect = undefined;
		hubMock.handle.onReconnected.mockReset();
		hubMock.handle.onReconnected.mockImplementation((callback: () => void) => {
			hubMock.reconnect = callback;
			return vi.fn();
		});
	});

	it("streams the node on the run hub and folds snapshot, reasoning delta and terminal like the chat does", async () => {
		const { result } = renderHook(() => useGraphWorkflowNodeActivity(runId, nodeKey, "invocation-1"));

		await waitFor(() => expect(hubMock.streams).toHaveLength(1));
		expect(hubMock.acquire).toHaveBeenCalledWith("graph-workflows/hub");
		expect(latest().args).toEqual(["StreamNodeActivity", runId, nodeKey]);
		expect(result.current.status).toBe("connecting");

		emit(frame({ type: "assistant-snapshot", sequence: 0, content: "", reasoning: "Weighing", contentOffset: 0 }));
		expect(result.current.status).toBe("live");
		expect(result.current.stream?.reasoning).toBe("Weighing");

		emit(frame({ sequence: 1, reasoningDelta: " the options", reasoningOffset: 8, contentOffset: 0 }));
		expect(result.current.stream?.reasoning).toBe("Weighing the options");
		expect(result.current.stream?.isActive).toBe(true);

		emit(
			frame({
				type: "assistant-completed",
				status: "completed",
				sequence: 2,
				content: "Done",
				reasoning: "Weighing the options",
				outputTokens: 42,
			}),
		);
		expect(result.current.status).toBe("ended");
		expect(result.current.stream?.isActive).toBe(false);
		expect(result.current.stream?.content).toBe("Done");
		expect(result.current.stream?.outputTokens).toBe(42);
	});

	it("reports the model the server resolved and keeps it across frames that do not repeat it", async () => {
		const { result } = renderHook(() => useGraphWorkflowNodeActivity(runId, nodeKey, "invocation-1"));
		await waitFor(() => expect(hubMock.streams).toHaveLength(1));
		expect(result.current.model).toBeUndefined();

		emit(frame({ type: "assistant-snapshot", sequence: 0, content: "", contentOffset: 0, model: "qwen3-8b" }));
		expect(result.current.model).toBe("qwen3-8b");

		emit(frame({ sequence: 1, delta: "Hi", contentOffset: 0 }));
		expect(result.current.model).toBe("qwen3-8b");
	});

	it("re-keys on a new invocation id and goes idle without one", async () => {
		const { result, rerender } = renderHook(({ invocationId }) => useGraphWorkflowNodeActivity(runId, nodeKey, invocationId), {
			initialProps: { invocationId: "invocation-1" as string | undefined },
		});
		await waitFor(() => expect(hubMock.streams).toHaveLength(1));
		emit(frame({ type: "assistant-snapshot", reasoning: "first attempt", content: "" }));
		const first = latest();

		rerender({ invocationId: "invocation-2" });
		await waitFor(() => expect(hubMock.streams).toHaveLength(2));
		expect(first.dispose).toHaveBeenCalled();
		expect(result.current.stream).toBeUndefined();
		emit(frame({ type: "assistant-snapshot", messageId: "invocation-2", reasoning: "second attempt", content: "" }));
		expect(result.current.stream?.reasoning).toBe("second attempt");

		rerender({ invocationId: undefined });
		expect(result.current).toEqual({ status: "idle" });
		expect(latest().dispose).toHaveBeenCalled();
		expect(hubMock.handle.release).toHaveBeenCalledTimes(2);
	});

	it("reads a refused open as unavailable, silently", async () => {
		const { result } = renderHook(() => useGraphWorkflowNodeActivity(runId, nodeKey, "invocation-1"));
		await waitFor(() => expect(hubMock.streams).toHaveLength(1));

		act(() => latest().observer.error(new Error("HubException: the node is not running")));

		await waitFor(() => expect(result.current).toEqual({ status: "unavailable", stream: undefined }));
	});

	it("re-opens after a reconnect and re-syncs from the snapshot", async () => {
		const { result } = renderHook(() => useGraphWorkflowNodeActivity(runId, nodeKey, "invocation-1"));
		await waitFor(() => expect(hubMock.streams).toHaveLength(1));
		emit(frame({ type: "assistant-snapshot", reasoning: "Before", content: "" }));

		// SignalR's real order: the stream errors while the state still reads `Connected`, and only then does the
		// reconnect move it to `Reconnecting`. The drop must not be taken for a refusal.
		await act(async () => {
			latest().observer.error(new Error("connection lost"));
			hubMock.connection.state = "Reconnecting";
			await Promise.resolve();
		});
		expect(result.current.status).toBe("live");

		hubMock.connection.state = "Connected";
		act(() => hubMock.reconnect?.());
		expect(hubMock.streams).toHaveLength(2);
		emit(frame({ type: "assistant-snapshot", reasoning: "Before and after", content: "" }));
		expect(result.current.stream?.reasoning).toBe("Before and after");
	});

	it("stops without re-opening when the first frame is a reconcile (the replay cap)", async () => {
		const { result } = renderHook(() => useGraphWorkflowNodeActivity(runId, nodeKey, "invocation-1"));
		await waitFor(() => expect(hubMock.streams).toHaveLength(1));

		emit(frame({ type: "assistant-reconcile" }));
		act(() => latest().observer.complete());

		expect(result.current).toEqual({ status: "unavailable", stream: undefined });
		act(() => hubMock.reconnect?.());
		expect(hubMock.connection.stream).toHaveBeenCalledTimes(1);
	});

	it("re-opens once on a mid-stream reconcile, and stops if the re-opened stream answers with one first", async () => {
		const { result } = renderHook(() => useGraphWorkflowNodeActivity(runId, nodeKey, "invocation-1"));
		await waitFor(() => expect(hubMock.streams).toHaveLength(1));
		emit(frame({ type: "assistant-snapshot", reasoning: "Before", content: "" }));

		emit(frame({ type: "assistant-reconcile" }));
		expect(hubMock.streams).toHaveLength(2);
		expect(hubMock.streams[0]?.dispose).toHaveBeenCalled();

		emit(frame({ type: "assistant-reconcile" }));
		expect(hubMock.streams).toHaveLength(2);
		expect(result.current.status).toBe("unavailable");
		expect(result.current.stream?.reasoning).toBe("Before");
	});
});

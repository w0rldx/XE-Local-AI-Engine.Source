import { afterEach, describe, expect, it, vi } from "vitest";

import { clientWatchdogFailureCategory, guardNodeChatStream, StreamWatchdogError, streamWatchdogNotice } from "@/features/chat/api/NodeChatStreamGuard";
import type { NodeChatStreamEventDto } from "@/features/chat/models/NodeChatStreamTypes";

function event(sequence: number, content: string): NodeChatStreamEventDto {
	return {
		type: "assistant-delta",
		conversationId: "conversation-1",
		messageId: "assistant-1",
		requestId: "request-1",
		status: "streaming",
		sequence,
		occurredAtUtc: 1_700_000_000_000 + sequence,
		delta: content,
		content,
	};
}

function phaseEvent(sequence: number, runtimePhase: string): NodeChatStreamEventDto {
	return {
		type: "assistant-phase",
		conversationId: "conversation-1",
		messageId: "assistant-1",
		requestId: "request-1",
		status: "streaming",
		sequence,
		occurredAtUtc: 1_700_000_000_000 + sequence,
		runtimePhase,
	};
}

// The server stamps the turn's effective whole-turn ceiling on `assistant-queued` (and `assistant-streaming`); the
// guard reads it off the wire to derive its own deadlines. `invocationTimeoutSeconds: undefined` models a server that
// never sends one (a resume re-attach, or a pre-field node).
function queuedEvent(sequence: number, invocationTimeoutSeconds?: number): NodeChatStreamEventDto {
	return {
		type: "assistant-queued",
		conversationId: "conversation-1",
		messageId: "assistant-1",
		requestId: "request-1",
		status: "queued",
		sequence,
		occurredAtUtc: 1_700_000_000_000 + sequence,
		invocationTimeoutSeconds,
	};
}

async function fromArray(events: NodeChatStreamEventDto[]): Promise<NodeChatStreamEventDto[]> {
	async function* source(): AsyncGenerator<NodeChatStreamEventDto> {
		for (const value of events) {
			yield value;
		}
	}
	const out: NodeChatStreamEventDto[] = [];
	for await (const value of guardNodeChatStream(source())) {
		out.push(value);
	}
	return out;
}

describe("guardNodeChatStream", () => {
	afterEach(() => {
		vi.useRealTimers();
	});

	it("emits events in ascending sequence order", async () => {
		const out = await fromArray([event(0, "a"), event(2, "c"), event(1, "b")]);
		expect(out.map((value) => value.sequence)).toEqual([0, 1, 2]);
		expect(out.map((value) => value.content)).toEqual(["a", "b", "c"]);
	});

	it("drops stale lower-sequence events replayed after a reconnect", async () => {
		const out = await fromArray([event(3, "d"), event(4, "e"), event(2, "stale"), event(5, "f")]);
		expect(out.map((value) => value.sequence)).toEqual([3, 4, 5]);
		expect(out.some((value) => value.content === "stale")).toBe(false);
	});

	it("flushes trailing buffered events when a gap never fills", async () => {
		// sequence 1 never arrives; 0 emits immediately, 2 and 3 are flushed at completion in order.
		const out = await fromArray([event(0, "a"), event(2, "c"), event(3, "d")]);
		expect(out.map((value) => value.sequence)).toEqual([0, 2, 3]);
	});

	it("fails with no-first-chunk when the first event never arrives", async () => {
		vi.useFakeTimers();
		async function* never(): AsyncGenerator<NodeChatStreamEventDto> {
			await new Promise<void>(() => {
				// never resolves
			});
		}

		const iterator = guardNodeChatStream(never(), { firstChunkTimeoutMs: 1_000 })[Symbol.asyncIterator]();
		const next = iterator.next().then(
			() => undefined,
			(error: unknown) => error,
		);
		await vi.advanceTimersByTimeAsync(1_000);

		const caught = await next;
		expect(caught).toBeInstanceOf(StreamWatchdogError);
		expect(caught).toMatchObject({ category: "no-first-chunk" });
	});

	it("fails with inter-chunk-stall when the stream goes silent mid-flight", async () => {
		vi.useFakeTimers();
		let release: (() => void) | undefined;
		async function* stalling(): AsyncGenerator<NodeChatStreamEventDto> {
			yield event(0, "a");
			await new Promise<void>((resolve) => {
				release = resolve;
			});
			yield event(1, "b");
		}

		const iterator = guardNodeChatStream(stalling(), { firstChunkTimeoutMs: 5_000, interChunkTimeoutMs: 1_000 })[
			Symbol.asyncIterator
		]();
		await expect(iterator.next()).resolves.toMatchObject({ value: { sequence: 0 }, done: false });

		const stalled = iterator.next().then(
			() => undefined,
			(error: unknown) => error,
		);
		await vi.advanceTimersByTimeAsync(1_000);

		const caught = await stalled;
		expect(caught).toMatchObject({ category: "inter-chunk-stall" });
		release?.();
	});

	it("does not false-fail during a silent cold model load (loading_model phase)", async () => {
		vi.useFakeTimers();
		let release: (() => void) | undefined;
		async function* loading(): AsyncGenerator<NodeChatStreamEventDto> {
			yield phaseEvent(0, "loading_model");
			await new Promise<void>((resolve) => {
				release = resolve;
			});
			yield event(1, "a");
		}

		const iterator = guardNodeChatStream(loading(), { firstChunkTimeoutMs: 5_000, interChunkTimeoutMs: 1_000 })[
			Symbol.asyncIterator
		]();
		await expect(iterator.next()).resolves.toMatchObject({ value: { sequence: 0 }, done: false });

		const pending = iterator.next();
		// Far past the normal 1 s inter-chunk deadline, but well within the extended cold-load window — the
		// watchdog must NOT fire while the model is still loading.
		await vi.advanceTimersByTimeAsync(200_000);
		release?.();
		await expect(pending).resolves.toMatchObject({ value: { sequence: 1 }, done: false });
	});

	it("still fails once the extended cold-load deadline elapses", async () => {
		vi.useFakeTimers();
		async function* loadingForever(): AsyncGenerator<NodeChatStreamEventDto> {
			yield phaseEvent(0, "loading_model");
			await new Promise<void>(() => {
				// never resolves — the load hangs past the readiness ceiling
			});
		}

		const iterator = guardNodeChatStream(loadingForever(), { firstChunkTimeoutMs: 5_000, interChunkTimeoutMs: 1_000 })[
			Symbol.asyncIterator
		]();
		await expect(iterator.next()).resolves.toMatchObject({ value: { sequence: 0 }, done: false });

		const stalled = iterator.next().then(
			() => undefined,
			(error: unknown) => error,
		);
		await vi.advanceTimersByTimeAsync(660_000);

		const caught = await stalled;
		expect(caught).toBeInstanceOf(StreamWatchdogError);
		expect(caught).toMatchObject({ category: "inter-chunk-stall" });
	});

	it("reverts to the normal inter-chunk deadline once generation starts", async () => {
		vi.useFakeTimers();
		async function* loadThenStall(): AsyncGenerator<NodeChatStreamEventDto> {
			yield phaseEvent(0, "loading_model");
			yield phaseEvent(1, "generating");
			yield event(2, "a");
			await new Promise<void>(() => {
				// never resolves — a genuine mid-generation stall
			});
		}

		const iterator = guardNodeChatStream(loadThenStall(), { firstChunkTimeoutMs: 5_000, interChunkTimeoutMs: 1_000 })[
			Symbol.asyncIterator
		]();
		await expect(iterator.next()).resolves.toMatchObject({ value: { sequence: 0 }, done: false });
		await expect(iterator.next()).resolves.toMatchObject({ value: { sequence: 1 }, done: false });
		await expect(iterator.next()).resolves.toMatchObject({ value: { sequence: 2 }, done: false });

		const stalled = iterator.next().then(
			() => undefined,
			(error: unknown) => error,
		);
		// Generation has begun, so the 1 s inter-chunk deadline applies again (not the extended one).
		await vi.advanceTimersByTimeAsync(1_000);

		const caught = await stalled;
		expect(caught).toBeInstanceOf(StreamWatchdogError);
		expect(caught).toMatchObject({ category: "inter-chunk-stall" });
	});

	it("keeps the default deadlines when no event carries the node timeout", async () => {
		vi.useFakeTimers();
		async function* silentAfterQueued(): AsyncGenerator<NodeChatStreamEventDto> {
			yield queuedEvent(0);
			await new Promise<void>(() => {
				// never resolves
			});
		}

		const iterator = guardNodeChatStream(silentAfterQueued())[Symbol.asyncIterator]();
		await expect(iterator.next()).resolves.toMatchObject({ value: { sequence: 0 }, done: false });

		const stalled = iterator.next().then(
			() => undefined,
			(error: unknown) => error,
		);
		// The unchanged 180 s inter-chunk constant still owns the deadline.
		await vi.advanceTimersByTimeAsync(179_999);
		await expect(Promise.race([stalled, Promise.resolve("pending")])).resolves.toBe("pending");
		await vi.advanceTimersByTimeAsync(1);
		expect(await stalled).toBeInstanceOf(StreamWatchdogError);
	});

	it("widens the deadline when the node timeout is raised above the default", async () => {
		vi.useFakeTimers();
		async function* silentAfterQueued(): AsyncGenerator<NodeChatStreamEventDto> {
			yield queuedEvent(0, 900);
			await new Promise<void>(() => {
				// never resolves — a turn the node is still willing to wait 900 s for
			});
		}

		const iterator = guardNodeChatStream(silentAfterQueued())[Symbol.asyncIterator]();
		await expect(iterator.next()).resolves.toMatchObject({ value: { sequence: 0 }, done: false });

		const stalled = iterator.next().then(
			() => undefined,
			(error: unknown) => error,
		);
		// Far past the 180 s constant: the client must NOT pre-empt the node's own 900 s ceiling.
		await vi.advanceTimersByTimeAsync(880_000);
		await expect(Promise.race([stalled, Promise.resolve("pending")])).resolves.toBe("pending");
		// 900 s + the 30 s grace for the node's terminal event to reach us.
		await vi.advanceTimersByTimeAsync(50_000);
		expect(await stalled).toBeInstanceOf(StreamWatchdogError);
	});

	it("never drops below the dead-transport constants when the node timeout is lowered", async () => {
		vi.useFakeTimers();
		async function* silentAfterQueued(): AsyncGenerator<NodeChatStreamEventDto> {
			yield queuedEvent(0, 5);
			await new Promise<void>(() => {
				// never resolves
			});
		}

		const iterator = guardNodeChatStream(silentAfterQueued())[Symbol.asyncIterator]();
		await expect(iterator.next()).resolves.toMatchObject({ value: { sequence: 0 }, done: false });

		const stalled = iterator.next().then(
			() => undefined,
			(error: unknown) => error,
		);
		// A 5 s node ceiling would derive a 35 s floor; the 180 s constant is the floor the guard keeps.
		await vi.advanceTimersByTimeAsync(179_999);
		await expect(Promise.race([stalled, Promise.resolve("pending")])).resolves.toBe("pending");
		await vi.advanceTimersByTimeAsync(1);
		expect(await stalled).toBeInstanceOf(StreamWatchdogError);
	});
});

describe("streamWatchdogNotice", () => {
	it("maps each watchdog category to its own translatable sentence", () => {
		expect(streamWatchdogNotice("no-first-chunk").key).toBe("pages.chat.error.clientWatchdogNoFirstChunk");
		expect(streamWatchdogNotice("inter-chunk-stall").key).toBe("pages.chat.error.clientWatchdogStall");
		expect(streamWatchdogNotice("no-first-chunk").key).not.toBe(streamWatchdogNotice("inter-chunk-stall").key);
	});

	it("reports a client-side give-up under its own reason code, never a node failure category", () => {
		expect(clientWatchdogFailureCategory).toBe("ClientWatchdog");
	});
});

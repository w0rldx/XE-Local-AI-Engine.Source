import { afterEach, describe, expect, it, vi } from "vitest";

import { guardNodeChatStream, StreamWatchdogError } from "@/features/chat/api/NodeChatStreamGuard";
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
});

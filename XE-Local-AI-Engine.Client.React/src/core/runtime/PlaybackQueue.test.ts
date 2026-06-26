import { describe, expect, it, vi } from "vitest";

import { PlaybackQueue, type QueueAudioContext } from "./PlaybackQueue";
import type { AudioChunk } from "./TtsProvider";

interface FakeSource {
	buffer: { readonly duration: number; copyToChannel: ReturnType<typeof vi.fn> } | null;
	onended: (() => void) | null;
	connect: ReturnType<typeof vi.fn>;
	disconnect: ReturnType<typeof vi.fn>;
	start: ReturnType<typeof vi.fn>;
	stop: ReturnType<typeof vi.fn>;
}

// A structural stand-in for an AudioContext. Not declared `implements QueueAudioContext` so the vitest Mock-typed
// spies (start/stop/etc.) need not satisfy the precise DOM signatures; it is cast at the PlaybackQueue boundary.
class FakeAudioContext {
	state: "suspended" | "running" | "closed" = "suspended";
	currentTime = 0;
	readonly destination = {};
	readonly sources: FakeSource[] = [];

	createBuffer(_channels: number, length: number, sampleRate: number) {
		return { duration: length / sampleRate, copyToChannel: vi.fn() };
	}

	createBufferSource(): FakeSource {
		const source: FakeSource = {
			buffer: null,
			onended: null,
			connect: vi.fn(),
			disconnect: vi.fn(),
			start: vi.fn(),
			stop: vi.fn(),
		};
		this.sources.push(source);
		return source;
	}

	resume(): Promise<void> {
		this.state = "running";
		return Promise.resolve();
	}

	suspend(): Promise<void> {
		this.state = "suspended";
		return Promise.resolve();
	}

	close(): Promise<void> {
		this.state = "closed";
		return Promise.resolve();
	}
}

function chunk(length: number, sampleRate = 24_000): AudioChunk {
	return { pcm: new Float32Array(length), sampleRate };
}

describe("PlaybackQueue scheduling", () => {
	it("schedules chunks gaplessly, advancing the nextTime cursor", async () => {
		const context = new FakeAudioContext();
		const queue = new PlaybackQueue(() => context as unknown as QueueAudioContext);
		await queue.resume();

		// 240 frames @ 24kHz = 0.01s; 480 frames = 0.02s.
		queue.enqueue(chunk(240));
		queue.enqueue(chunk(480));

		expect(context.sources).toHaveLength(2);
		expect(context.sources[0]?.start).toHaveBeenCalledWith(0);
		expect(context.sources[1]?.start).toHaveBeenCalledWith(0.01);
	});

	it("buffers chunks while suspended and plays them on resume (autoplay-gesture gating)", async () => {
		const context = new FakeAudioContext();
		const queue = new PlaybackQueue(() => context as unknown as QueueAudioContext);

		queue.enqueue(chunk(240));
		queue.enqueue(chunk(240));
		expect(context.sources).toHaveLength(0);

		await queue.resume();

		expect(context.sources).toHaveLength(2);
	});

	it("stop() halts all live nodes and resets the cursor (barge-in)", async () => {
		const context = new FakeAudioContext();
		const queue = new PlaybackQueue(() => context as unknown as QueueAudioContext);
		await queue.resume();
		queue.enqueue(chunk(240));
		queue.enqueue(chunk(240));

		queue.stop();

		expect(context.sources[0]?.stop).toHaveBeenCalled();
		expect(context.sources[1]?.stop).toHaveBeenCalled();

		// After reset the cursor is back at currentTime, so the next chunk starts at 0, not stacked behind the old ones.
		queue.enqueue(chunk(240));
		expect(context.sources[2]?.start).toHaveBeenCalledWith(0);
	});
});

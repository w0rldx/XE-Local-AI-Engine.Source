// Gapless Web Audio scheduler for streamed TTS chunks.
//
// Owns ONE long-lived AudioContext for the session — never `close()`d per turn (closing is unrecoverable). The
// context starts suspended and only produces sound after `resume()` is called from a user gesture (browser autoplay
// policy); `enqueue` before that point buffers chunks and never throws. Scheduling is
// gapless: a `nextTime` cursor places each chunk exactly where the previous one ends, with a fresh
// AudioBufferSourceNode per chunk. `stop()` halts every live node and resets the cursor for barge-in.

import type { AudioChunk } from "./TtsProvider";

// Minimal structural types for the Web Audio surface used here. The real `AudioContext` / `AudioBuffer` /
// `AudioBufferSourceNode` satisfy these, so `new AudioContext()` is assignable; tests inject lightweight fakes.
interface QueueAudioBuffer {
	readonly duration: number;
	copyToChannel(source: Float32Array, channelNumber: number): void;
}

interface QueueAudioBufferSourceNode {
	buffer: QueueAudioBuffer | null;
	// Typed to accept an Event arg so a real `AudioBufferSourceNode.onended` (which receives one) stays assignable.
	onended: ((event: Event) => void) | null;
	connect(destination: unknown): void;
	disconnect(): void;
	start(when?: number): void;
	stop(when?: number): void;
}

export interface QueueAudioContext {
	// Mirrors the DOM `AudioContextState` (including Safari's "interrupted") so a real `AudioContext` is assignable.
	readonly state: "suspended" | "running" | "closed" | "interrupted";
	readonly currentTime: number;
	readonly destination: unknown;
	createBuffer(numberOfChannels: number, length: number, sampleRate: number): QueueAudioBuffer;
	createBufferSource(): QueueAudioBufferSourceNode;
	resume(): Promise<void>;
	suspend(): Promise<void>;
	close(): Promise<void>;
}

/** Factory for the audio context — defaults to a real `AudioContext`; tests inject a fake. */
export type AudioContextFactory = () => QueueAudioContext;

const defaultAudioContextFactory: AudioContextFactory = () => new AudioContext();

export class PlaybackQueue {
	private readonly context: QueueAudioContext;
	private readonly liveNodes = new Set<QueueAudioBufferSourceNode>();
	// Chunks enqueued before the context is running are held here and drained on `resume()`.
	private pending: AudioChunk[] = [];
	// Schedule cursor: the context time at which the next chunk should start, for seam-free playback.
	private nextTime = 0;

	constructor(audioContextFactory: AudioContextFactory = defaultAudioContextFactory) {
		this.context = audioContextFactory();
	}

	/** Whether playback is currently active (used by callers to gate UI / gesture prompts). */
	get isRunning(): boolean {
		return this.context.state === "running";
	}

	/**
	 * Queues a chunk for playback. While the context is not running (suspended pre-gesture) the chunk is buffered and
	 * played once `resume()` is called — this never throws, satisfying the autoplay-gesture invariant.
	 */
	enqueue(chunk: AudioChunk): void {
		if (this.context.state !== "running") {
			this.pending.push(chunk);
			return;
		}

		this.scheduleChunk(chunk);
	}

	/** Resumes the context on a user gesture, aligns the cursor to now, and drains anything buffered while suspended. */
	async resume(): Promise<void> {
		await this.context.resume();
		this.nextTime = Math.max(this.nextTime, this.context.currentTime);

		const buffered = this.pending;
		this.pending = [];
		for (const chunk of buffered) {
			this.scheduleChunk(chunk);
		}
	}

	/** Pauses playback without tearing down the context (resumable). */
	async suspend(): Promise<void> {
		await this.context.suspend();
	}

	/** Barge-in: stops every live source, clears the buffer, and resets the schedule cursor to now. */
	stop(): void {
		for (const node of this.liveNodes) {
			this.safelyStop(node);
		}

		this.liveNodes.clear();
		this.pending = [];
		this.nextTime = this.context.currentTime;
	}

	/** App-teardown only — closes the context permanently. NEVER call this per turn. */
	async close(): Promise<void> {
		this.stop();
		await this.context.close();
	}

	private scheduleChunk(chunk: AudioChunk): void {
		const buffer = this.context.createBuffer(1, chunk.pcm.length, chunk.sampleRate);
		buffer.copyToChannel(chunk.pcm, 0);

		const source = this.context.createBufferSource();
		source.buffer = buffer;
		source.connect(this.context.destination);

		const startAt = Math.max(this.nextTime, this.context.currentTime);
		source.start(startAt);
		this.nextTime = startAt + buffer.duration;

		this.liveNodes.add(source);
		source.onended = () => {
			this.liveNodes.delete(source);
			this.safelyDisconnect(source);
		};
	}

	private safelyStop(node: QueueAudioBufferSourceNode): void {
		try {
			node.stop();
		} catch {
			// A node already stopped / never started throws InvalidStateError — harmless during barge-in.
		}

		this.safelyDisconnect(node);
	}

	private safelyDisconnect(node: QueueAudioBufferSourceNode): void {
		try {
			node.disconnect();
		} catch {
			// Disconnecting an already-disconnected node throws — ignore.
		}
	}
}

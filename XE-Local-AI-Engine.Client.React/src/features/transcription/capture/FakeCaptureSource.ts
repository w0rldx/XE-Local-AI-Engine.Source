/**
 * The hand-written capture fake the orchestration and view tests drive (S4 plan §4.2).
 *
 * Rung 4 of the repo's mock order, and justified: no capture seam existed before this slice, jsdom has neither
 * `AudioContext` nor `navigator.mediaDevices`, and `vi.fn()` cannot model "start, then emit these three frames on
 * demand". It lives beside the interface it fakes rather than in `src/test/`.
 */

import type { CaptureChannel, CaptureError, CaptureFrame, CaptureSource } from "@/features/transcription/capture/CaptureSource";

export interface FakeCaptureSourceOptions {
	/** When set, `start` rejects with this error instead of succeeding — the `no-audio-track` and `permission-denied` paths. */
	readonly failWith?: CaptureError;
}

export class FakeCaptureSource implements CaptureSource {
	readonly channel: CaptureChannel;
	/** True once `start` has resolved. A source whose `start` rejected never reports started. */
	started = false;
	stopped = false;
	/** Counts every `start` call, including the ones that reject — call ordering is what the R39a tests assert. */
	startCalls = 0;
	private readonly failWith: CaptureError | undefined;
	private onFrame: ((frame: CaptureFrame) => void) | null = null;

	constructor(channel: CaptureChannel, options?: FakeCaptureSourceOptions) {
		this.channel = channel;
		this.failWith = options?.failWith;
	}

	async start(onFrame: (frame: CaptureFrame) => void): Promise<void> {
		this.startCalls += 1;
		if (this.failWith !== undefined) {
			throw this.failWith;
		}
		this.onFrame = onFrame;
		this.started = true;
	}

	/** Hands one frame to whatever `start` was given. A no-op before `start` or after `stop`. */
	emit(pcm: Int16Array): void {
		this.onFrame?.({ channel: this.channel, pcm });
	}

	async stop(): Promise<void> {
		this.stopped = true;
		this.onFrame = null;
	}
}

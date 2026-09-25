/**
 * The microphone capture source (S4 plan §2.2): `getUserMedia` plus the shared PCM graph.
 */

import {
	CaptureError,
	type CaptureChannel,
	type CaptureFrame,
	type CaptureSource,
} from "@/features/transcription/capture/CaptureSource";
import { startPcmCapture } from "@/features/transcription/capture/PcmCapture";

/**
 * One bound for the whole start: the permission prompt, `getUserMedia`, `addModule` and `AudioContext.resume()` can each
 * wait for ever (a resume outside a user gesture never settles), and a start that never settles captures nothing and
 * says nothing.
 */
export const MICROPHONE_START_TIMEOUT_MS = 20_000;

export class MicrophoneCaptureSource implements CaptureSource {
	readonly channel: CaptureChannel;
	private readonly deviceId: string | undefined;
	private dispose: (() => Promise<void>) | null = null;
	// Bumped by `stop` and by the start timeout: a start that settles under an older generation releases what it got.
	private generation = 0;
	// The stream between `getUserMedia` and a started graph. A graph that never starts never hands back a disposer, so the
	// timeout and `stop` release this instead, and abort the half-built graph's context with it.
	private pending: { readonly stream: MediaStream; readonly abort: AbortController } | null = null;

	constructor(channel: CaptureChannel, deviceId?: string) {
		this.channel = channel;
		this.deviceId = deviceId;
	}

	async start(onFrame: (frame: CaptureFrame) => void): Promise<void> {
		if (navigator.mediaDevices === undefined) {
			throw new CaptureError("unsupported", "navigator.mediaDevices is unavailable — the page is not in a secure context.");
		}

		const generation = ++this.generation;
		let timer: ReturnType<typeof setTimeout> | undefined;
		const timedOut = new Promise<never>((_, reject) => {
			timer = setTimeout(() => {
				this.generation += 1;
				this.releasePending();
				reject(new CaptureError("start-timeout", `The microphone did not start within ${MICROPHONE_START_TIMEOUT_MS} ms.`));
			}, MICROPHONE_START_TIMEOUT_MS);
		});
		const acquiring = this.acquire(onFrame, generation);
		// A start that fails after the timeout already reported is not a second error.
		acquiring.catch(() => undefined);
		try {
			await Promise.race([acquiring, timedOut]);
		} finally {
			clearTimeout(timer);
		}
	}

	private async acquire(onFrame: (frame: CaptureFrame) => void, generation: number): Promise<void> {
		let stream: MediaStream;
		try {
			// No echoCancellation/noiseSuppression overrides: the browser defaults are what a meeting participant
			// expects, and whisper is not fed through WebRTC processing.
			stream = await navigator.mediaDevices.getUserMedia({
				audio: this.deviceId === undefined ? true : { deviceId: { exact: this.deviceId } },
			});
		} catch (error) {
			throw toMicrophoneCaptureError(error);
		}

		if (generation !== this.generation) {
			stopTracks(stream);
			return;
		}

		const pending = { stream, abort: new AbortController() };
		this.pending = pending;
		try {
			const dispose = await startPcmCapture(stream, (pcm) => onFrame({ channel: this.channel, pcm }), pending.abort.signal);
			if (this.pending === pending) {
				this.pending = null;
			}
			if (generation !== this.generation) {
				await dispose();
				return;
			}
			this.dispose = dispose;
		} catch (error) {
			// A worklet that failed to start must not leave the microphone hot.
			if (this.pending === pending) {
				this.pending = null;
			}
			stopTracks(stream);
			throw error;
		}
	}

	private releasePending(): void {
		const pending = this.pending;
		this.pending = null;
		if (pending !== null) {
			stopTracks(pending.stream);
			pending.abort.abort();
		}
	}

	async stop(): Promise<void> {
		this.generation += 1;
		this.releasePending();
		const dispose = this.dispose;
		this.dispose = null;
		await dispose?.();
	}
}

function stopTracks(stream: MediaStream): void {
	for (const track of stream.getTracks()) {
		track.stop();
	}
}

function toMicrophoneCaptureError(error: unknown): CaptureError {
	const name = error instanceof Error ? error.name : "";
	const message = error instanceof Error ? error.message : String(error);
	switch (name) {
		case "NotAllowedError":
		case "SecurityError":
			return new CaptureError("permission-denied", `Microphone access was refused: ${message}`);
		case "NotFoundError":
		case "OverconstrainedError":
			return new CaptureError("no-device", `No usable microphone: ${message}`);
		default:
			return new CaptureError("unsupported", `getUserMedia failed: ${message}`);
	}
}

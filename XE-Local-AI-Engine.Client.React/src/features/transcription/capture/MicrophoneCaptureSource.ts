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

export class MicrophoneCaptureSource implements CaptureSource {
	readonly channel: CaptureChannel;
	private readonly deviceId: string | undefined;
	private dispose: (() => Promise<void>) | null = null;

	constructor(channel: CaptureChannel, deviceId?: string) {
		this.channel = channel;
		this.deviceId = deviceId;
	}

	async start(onFrame: (frame: CaptureFrame) => void): Promise<void> {
		if (navigator.mediaDevices === undefined) {
			throw new CaptureError("unsupported", "navigator.mediaDevices is unavailable — the page is not in a secure context.");
		}

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

		try {
			this.dispose = await startPcmCapture(stream, (pcm) => onFrame({ channel: this.channel, pcm }));
		} catch (error) {
			// A worklet that failed to start must not leave the microphone hot.
			for (const track of stream.getTracks()) {
				track.stop();
			}
			throw error;
		}
	}

	async stop(): Promise<void> {
		const dispose = this.dispose;
		this.dispose = null;
		await dispose?.();
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

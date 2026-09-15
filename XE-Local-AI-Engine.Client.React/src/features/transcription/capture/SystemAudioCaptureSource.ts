/**
 * The system-audio capture source (S4 plan §2.2, ruling R19): `getDisplayMedia` with the video thrown away, and a
 * loud failure when the picked surface came back without an audio track.
 */

import {
	CaptureError,
	type CaptureChannel,
	type CaptureFrame,
	type CaptureSource,
} from "@/features/transcription/capture/CaptureSource";
import { startPcmCapture } from "@/features/transcription/capture/PcmCapture";

/**
 * `systemAudio` and `selfBrowserSurface` are Screen Capture options that this TypeScript's `lib.dom` does not
 * carry yet. Widening locally and casting once at the call is the sanctioned way to pass them: a
 * `@ts-expect-error` would silently stop protecting the rest of the argument, and a `@types` package would be a
 * dependency for two string fields.
 */
interface SystemDisplayMediaOptions extends DisplayMediaStreamOptions {
	/** A HINT the OS may ignore — hence the audio-track check below. */
	readonly systemAudio?: "include" | "exclude";
	/** Never offer this app's own tab. */
	readonly selfBrowserSurface?: "include" | "exclude";
}

export class SystemAudioCaptureSource implements CaptureSource {
	readonly channel: CaptureChannel;
	private dispose: (() => Promise<void>) | null = null;

	constructor(channel: CaptureChannel) {
		this.channel = channel;
	}

	async start(onFrame: (frame: CaptureFrame) => void): Promise<void> {
		if (navigator.mediaDevices === undefined) {
			throw new CaptureError("unsupported", "navigator.mediaDevices is unavailable — the page is not in a secure context.");
		}

		// R39a: `getDisplayMedia` requires transient user activation AT THE MOMENT IT IS INVOKED, and the Screen
		// Capture specification requires rejection when that activation is absent. The picker promise is therefore
		// created here, synchronously, before this method's first `await` — anything awaited first (the live/start
		// endpoint, a permission prompt, `addModule`) can outlive the activation window and the picker never
		// appears. The same constraint reaches the call site: `useLiveCapture.start` must be invoked directly from
		// the click handler, with nothing awaited in between.
		const options: SystemDisplayMediaOptions = {
			// Required: no browser grants audio-only display capture.
			video: true,
			audio: true,
			systemAudio: "include",
			selfBrowserSurface: "exclude",
		};
		const picked = navigator.mediaDevices.getDisplayMedia(options as DisplayMediaStreamOptions);

		let stream: MediaStream;
		try {
			stream = await picked;
		} catch (error) {
			throw toDisplayCaptureError(error);
		}

		// Immediately: we never look at the picture.
		for (const track of stream.getVideoTracks()) {
			track.stop();
		}

		if (stream.getAudioTracks().length === 0) {
			// R19's safety net. Every track goes, or the screen-share indicator stays lit over a session that
			// never started.
			for (const track of stream.getTracks()) {
				track.stop();
			}
			throw new CaptureError("no-audio-track", "The shared surface came back without an audio track.");
		}

		try {
			this.dispose = await startPcmCapture(stream, (pcm) => onFrame({ channel: this.channel, pcm }));
		} catch (error) {
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

function toDisplayCaptureError(error: unknown): CaptureError {
	const name = error instanceof Error ? error.name : "";
	const message = error instanceof Error ? error.message : String(error);
	// A cancelled picker and a blocked one are the same `NotAllowedError`; both are the user declining to share.
	if (name === "NotAllowedError" || name === "SecurityError") {
		return new CaptureError("permission-denied", `Screen sharing was refused: ${message}`);
	}
	if (name === "NotFoundError") {
		return new CaptureError("no-device", `No shareable surface: ${message}`);
	}
	return new CaptureError("unsupported", `getDisplayMedia failed: ${message}`);
}

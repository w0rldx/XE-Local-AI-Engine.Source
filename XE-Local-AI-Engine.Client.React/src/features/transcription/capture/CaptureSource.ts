/**
 * The browser-side capture seam for live transcription (S4 plan §2.1).
 *
 * A `CaptureSource` turns one browser media stream into 16 kHz mono int16 frames and hands them to a callback;
 * `useLiveCapture` forwards those frames to the transcription hub. The seam exists so the view can be driven by
 * a hand-written fake under jsdom, which has neither `AudioContext` nor `navigator.mediaDevices`.
 */

/** The lane a frame belongs to. `mono` for a single source; `you`/`others` when microphone and system audio run side by side (D2). */
export type CaptureChannel = "mono" | "you" | "others";

export interface CaptureFrame {
	readonly channel: CaptureChannel;
	/** 16 kHz mono little-endian int16. One frame is 250 ms (4 000 samples); never more than 16 384 samples. */
	readonly pcm: Int16Array;
}

/**
 * What the UI switches on. The message on a `CaptureError` is for the console only: every user-facing string is
 * an i18n key chosen from the code, never the browser's own untranslated, version-dependent `error.message`.
 */
export type CaptureErrorCode =
	/** No `navigator.mediaDevices` — an insecure context. */
	| "unsupported"
	/** `NotAllowedError` / `SecurityError`. */
	| "permission-denied"
	/** `NotFoundError` / `OverconstrainedError`, or an empty audio-input list. */
	| "no-device"
	/** R19: `getDisplayMedia` returned a stream with no audio track. */
	| "no-audio-track"
	/** `AudioContext` construction or `audioWorklet.addModule` failed. */
	| "worklet-failed"
	/** R34a: the hub send limit was hit or the server reported `Overloaded`; frames are never dropped silently. */
	| "overloaded"
	/**
	 * The hub transport is not connected, so the frame could not be sent at all. Handled exactly like `overloaded`
	 * — capture stops — but it is a different diagnosis, and the diagnosis is the only thing the operator acts on.
	 */
	| "disconnected";

export class CaptureError extends Error {
	readonly code: CaptureErrorCode;

	constructor(code: CaptureErrorCode, message: string) {
		super(message);
		this.name = "CaptureError";
		this.code = code;
	}
}

export interface CaptureSource {
	readonly channel: CaptureChannel;
	/** Acquires the media and starts emitting frames. Rejects with a `CaptureError`. */
	start(onFrame: (frame: CaptureFrame) => void): Promise<void>;
	/** Stops every track and releases the audio graph. Idempotent. */
	stop(): Promise<void>;
}

export interface AudioInputDevice {
	readonly deviceId: string;
	/** Empty until a microphone permission has been granted once — the picker shows a hint, not blank rows. */
	readonly label: string;
}

export async function listAudioInputDevices(): Promise<readonly AudioInputDevice[]> {
	if (navigator.mediaDevices === undefined) {
		return [];
	}
	const devices = await navigator.mediaDevices.enumerateDevices();
	return devices
		.filter((device) => device.kind === "audioinput")
		.map((device) => ({ deviceId: device.deviceId, label: device.label }));
}

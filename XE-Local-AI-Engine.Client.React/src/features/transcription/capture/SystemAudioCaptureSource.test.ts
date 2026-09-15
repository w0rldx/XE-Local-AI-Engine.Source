import { afterEach, describe, expect, it, vi } from "vitest";

import { CaptureError } from "@/features/transcription/capture/CaptureSource";
import { SystemAudioCaptureSource } from "@/features/transcription/capture/SystemAudioCaptureSource";

interface StubTrack {
	kind: "audio" | "video";
	stop: () => void;
	stopped: boolean;
}

function createTrack(kind: "audio" | "video"): StubTrack {
	const track: StubTrack = {
		kind,
		stopped: false,
		stop: () => {
			track.stopped = true;
		},
	};
	return track;
}

function createStream(tracks: StubTrack[]): MediaStream {
	return {
		getTracks: () => tracks,
		getAudioTracks: () => tracks.filter((track) => track.kind === "audio"),
		getVideoTracks: () => tracks.filter((track) => track.kind === "video"),
	} as unknown as MediaStream;
}

function installMediaDevices(mediaDevices: unknown): void {
	Object.defineProperty(navigator, "mediaDevices", { value: mediaDevices, configurable: true, writable: true });
}

function failWith(name: string): Error {
	const error = new Error(`${name} raised by the stub`);
	error.name = name;
	return error;
}

afterEach(() => {
	Reflect.deleteProperty(navigator, "mediaDevices");
});

describe("SystemAudioCaptureSource", () => {
	it("reports 'unsupported' in an insecure context, where mediaDevices is absent", async () => {
		await expect(new SystemAudioCaptureSource("others").start(() => undefined)).rejects.toMatchObject({
			code: "unsupported",
		});
	});

	// Plan §4.2: WhenTheDisplayStreamHasNoAudioTrack_ThrowsNoAudioTrackAndStopsEveryTrack.
	// `systemAudio: "include"` is a hint the OS may ignore, so this is the branch that fires whenever the platform
	// (Linux Chrome, at time of writing) cannot deliver system audio. Leaving a track running here would keep the
	// screen-share indicator lit over a session that never started.
	it("throws 'no-audio-track' and stops every track when the shared surface has no audio", async () => {
		const video = createTrack("video");
		installMediaDevices({ getDisplayMedia: () => Promise.resolve(createStream([video])) });

		const rejection = await new SystemAudioCaptureSource("others").start(() => undefined).catch((error: unknown) => error);

		expect(rejection).toBeInstanceOf(CaptureError);
		expect(rejection).toMatchObject({ code: "no-audio-track" });
		expect(video.stopped).toBe(true);
	});

	it("maps a cancelled picker to 'permission-denied'", async () => {
		installMediaDevices({ getDisplayMedia: () => Promise.reject(failWith("NotAllowedError")) });

		await expect(new SystemAudioCaptureSource("others").start(() => undefined)).rejects.toMatchObject({
			code: "permission-denied",
		});
	});

	it("asks for system audio and excludes this app's own surface", async () => {
		const getDisplayMedia = vi.fn(() => Promise.resolve(createStream([createTrack("video")])));
		installMediaDevices({ getDisplayMedia });

		await new SystemAudioCaptureSource("others").start(() => undefined).catch(() => undefined);

		expect(getDisplayMedia).toHaveBeenCalledWith({
			video: true,
			audio: true,
			systemAudio: "include",
			selfBrowserSurface: "exclude",
		});
	});

	// R39a: `getDisplayMedia` needs transient user activation at the moment it is invoked, so the picker promise
	// must be created before `start` yields. Awaiting anything first — the live/start endpoint, `addModule` — can
	// outlive the activation window and the picker never appears.
	it("invokes the picker synchronously, before start's first await", () => {
		const getDisplayMedia = vi.fn(() => Promise.resolve(createStream([createTrack("video")])));
		installMediaDevices({ getDisplayMedia });

		const pending = new SystemAudioCaptureSource("others").start(() => undefined);

		expect(getDisplayMedia).toHaveBeenCalledTimes(1);
		return expect(pending).rejects.toMatchObject({ code: "no-audio-track" });
	});

	it("stops the picture immediately and keeps only the audio track alive", async () => {
		const video = createTrack("video");
		const audio = createTrack("audio");
		installMediaDevices({ getDisplayMedia: () => Promise.resolve(createStream([video, audio])) });
		// No audio graph is stubbed, so `startPcmCapture` fails at the context — which is the failure path this
		// case also needs: a worklet that cannot start must not leave the screen share running.
		vi.stubGlobal("AudioContext", undefined);

		await expect(new SystemAudioCaptureSource("others").start(() => undefined)).rejects.toMatchObject({
			code: "worklet-failed",
		});
		expect(video.stopped).toBe(true);
		expect(audio.stopped).toBe(true);
	});

	it("tolerates a stop before a start", async () => {
		await expect(new SystemAudioCaptureSource("others").stop()).resolves.toBeUndefined();
	});
});

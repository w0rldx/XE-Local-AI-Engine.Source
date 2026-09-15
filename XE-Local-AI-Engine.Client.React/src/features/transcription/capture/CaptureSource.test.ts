import { afterEach, describe, expect, it } from "vitest";

import { listAudioInputDevices } from "@/features/transcription/capture/CaptureSource";

function installMediaDevices(mediaDevices: unknown): void {
	Object.defineProperty(navigator, "mediaDevices", { value: mediaDevices, configurable: true, writable: true });
}

afterEach(() => {
	Reflect.deleteProperty(navigator, "mediaDevices");
});

describe("listAudioInputDevices", () => {
	it("returns nothing in an insecure context rather than throwing", async () => {
		await expect(listAudioInputDevices()).resolves.toEqual([]);
	});

	it("keeps only the audio inputs, dropping outputs and cameras", async () => {
		installMediaDevices({
			enumerateDevices: () =>
				Promise.resolve([
					{ deviceId: "mic-1", kind: "audioinput", label: "Headset" },
					{ deviceId: "cam-1", kind: "videoinput", label: "Webcam" },
					{ deviceId: "out-1", kind: "audiooutput", label: "Speakers" },
				]),
		});

		await expect(listAudioInputDevices()).resolves.toEqual([{ deviceId: "mic-1", label: "Headset" }]);
	});

	// Labels stay empty until a microphone permission has been granted once. The picker shows a "grant access to
	// see device names" hint for that case rather than a list of blank rows, so the empty label must survive here.
	it("passes an unlabelled device through so the picker can show its hint", async () => {
		installMediaDevices({
			enumerateDevices: () => Promise.resolve([{ deviceId: "mic-1", kind: "audioinput", label: "" }]),
		});

		await expect(listAudioInputDevices()).resolves.toEqual([{ deviceId: "mic-1", label: "" }]);
	});
});

// @vitest-environment jsdom

import { afterEach, describe, expect, it, vi } from "vitest";

import { detectVoiceCapabilities, detectWebSpeech, resetVoiceCapabilitiesCache } from "./CapabilityDetector";

const voice = {
	name: "Local English",
	lang: "en-US",
	voiceURI: "local-en",
	localService: true,
	default: true,
} as SpeechSynthesisVoice;

function setSpeechSynthesis(value: SpeechSynthesis | undefined): void {
	Object.defineProperty(globalThis, "speechSynthesis", { value, configurable: true });
}

describe("Web Speech capability detection", () => {
	afterEach(() => {
		vi.restoreAllMocks();
		resetVoiceCapabilitiesCache();
		Reflect.deleteProperty(globalThis, "speechSynthesis");
	});

	it("reports unavailable without the browser speech engine", async () => {
		setSpeechSynthesis(undefined);
		await expect(detectWebSpeech(0)).resolves.toEqual({ available: false, voices: [] });
	});

	it("returns installed voices without probing neural inference capabilities", async () => {
		setSpeechSynthesis({ getVoices: () => [voice] } as SpeechSynthesis);
		await expect(detectVoiceCapabilities()).resolves.toEqual({
			webSpeech: {
				available: true,
				voices: [{ voiceId: "local-en", name: "Local English", lang: "en-US", localService: true }],
			},
		});
	});
});

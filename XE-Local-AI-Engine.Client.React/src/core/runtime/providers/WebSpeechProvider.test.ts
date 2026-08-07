import { describe, expect, it, vi } from "vitest";

import { type SpeechSynthesisLike, WebSpeechProvider } from "./WebSpeechProvider";

// Fake OS voice catalog. "Google Deutsch" is a NETWORK (localService:false) German voice; "Anna" is an on-device
// German voice; "Daniel" is an English on-device voice. This lets us assert that an explicit voiceId beats the
// on-device preference ("selected voice always wins") while the language fallback still prefers on-device voices.
const voices = [
	{ name: "Google Deutsch", lang: "de-DE", voiceURI: "Google Deutsch", localService: false, default: false },
	{ name: "Anna", lang: "de-DE", voiceURI: "urn:moz-tts:anna", localService: true, default: false },
	{ name: "Daniel", lang: "en-GB", voiceURI: "urn:moz-tts:daniel", localService: true, default: false },
] as unknown as SpeechSynthesisVoice[];

function makeUtterance(text: string): SpeechSynthesisUtterance {
	return { text, voice: null, lang: "", rate: 1 } as unknown as SpeechSynthesisUtterance;
}

function makeProvider(): { provider: WebSpeechProvider; spoken: SpeechSynthesisUtterance[] } {
	const spoken: SpeechSynthesisUtterance[] = [];
	const synthesis: SpeechSynthesisLike = {
		getVoices: () => voices,
		speak: (utterance) => spoken.push(utterance),
		cancel: () => undefined,
	};
	const provider = new WebSpeechProvider({ synthesis, createUtterance: makeUtterance });
	return { provider, spoken };
}

describe("WebSpeechProvider voice selection", () => {
	it("honors voiceId by voiceURI even when the engine language differs from the pick", () => {
		const { provider, spoken } = makeProvider();
		provider.synthesize("Hallo Welt.", { language: "de", voiceId: "urn:moz-tts:anna" });
		expect(spoken[0]?.voice?.name).toBe("Anna");
		expect(spoken[0]?.lang).toBe("de-DE");
	});

	it("honors voiceId by name and lets the selected voice win over the on-device preference", () => {
		const { provider, spoken } = makeProvider();
		// "Google Deutsch" is a network voice; selecting it explicitly must beat the localService "Anna".
		provider.synthesize("Hallo Welt.", { language: "de", voiceId: "Google Deutsch" });
		expect(spoken[0]?.voice?.name).toBe("Google Deutsch");
	});

	it("falls back to the on-device language-prefix pick when voiceId maps to no OS voice", () => {
		const { provider, spoken } = makeProvider();
		const fetchSpy = vi.spyOn(globalThis, "fetch");
		// An old logical voice id is not an OS voice name — so the language pick takes over and
		// prefers the on-device German voice (Anna) over the network one (Google Deutsch).
		provider.synthesize("Hallo Welt.", { language: "de", voiceId: "af_heart" });
		expect(spoken[0]?.voice?.name).toBe("Anna");
		expect(fetchSpy).not.toHaveBeenCalled();
		fetchSpy.mockRestore();
	});

	it("sets only the requested lang when neither voiceId nor language matches any OS voice", () => {
		const { provider, spoken } = makeProvider();
		provider.synthesize("Bonjour.", { language: "fr", voiceId: "af_heart" });
		expect(spoken[0]?.voice).toBeNull();
		expect(spoken[0]?.lang).toBe("fr");
	});
});

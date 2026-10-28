// Web Speech capability probing. Voices may populate asynchronously, so detection waits for one bounded
// `voiceschanged` notification before returning the session snapshot.

export interface WebSpeechVoiceInfo {
	readonly voiceId: string;
	readonly name: string;
	readonly lang: string;
	readonly localService: boolean;
}

export interface WebSpeechCapability {
	readonly available: boolean;
	readonly voices: readonly WebSpeechVoiceInfo[];
}

export interface VoiceCapabilities {
	readonly webSpeech: WebSpeechCapability;
}

function readWebSpeechVoices(): WebSpeechVoiceInfo[] {
	const voices = globalThis.speechSynthesis?.getVoices() ?? [];
	return voices.map((voice) => ({
		voiceId: voice.voiceURI,
		name: voice.name,
		lang: voice.lang,
		localService: voice.localService,
	}));
}

export async function detectWebSpeech(voicesTimeoutMs = 1_000): Promise<WebSpeechCapability> {
	const synthesis = globalThis.speechSynthesis;
	if (!("speechSynthesis" in globalThis) || !synthesis) {
		return { available: false, voices: [] };
	}

	const initialVoices = readWebSpeechVoices();
	if (initialVoices.length > 0) {
		return { available: true, voices: initialVoices };
	}

	await new Promise<void>((resolve) => {
		const timer = setTimeout(resolve, voicesTimeoutMs);
		synthesis.addEventListener(
			"voiceschanged",
			() => {
				clearTimeout(timer);
				resolve();
			},
			{ once: true },
		);
	});

	return { available: true, voices: readWebSpeechVoices() };
}

let cachedCapabilities: Promise<VoiceCapabilities> | undefined;

export function detectVoiceCapabilities(voicesTimeoutMs?: number): Promise<VoiceCapabilities> {
	cachedCapabilities ??= detectWebSpeech(voicesTimeoutMs).then((webSpeech) => ({ webSpeech }));
	return cachedCapabilities;
}

export function resetVoiceCapabilitiesCache(): void {
	cachedCapabilities = undefined;
}

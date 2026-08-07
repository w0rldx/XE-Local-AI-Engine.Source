import { describe, expect, it, vi } from "vitest";

import type { AudioChunk, TtsProvider, VoiceSynthesisOptions } from "./TtsProvider";
import { VoiceRuntime } from "./VoiceRuntime";

const emptyStream: AsyncIterable<AudioChunk> = {
	[Symbol.asyncIterator]: () => ({ next: () => Promise.resolve({ value: undefined, done: true }) }),
};

function makeProvider(overrides: Partial<TtsProvider> = {}): TtsProvider {
	return {
		id: "web-speech",
		producesPcm: false,
		init: vi.fn(() => Promise.resolve()),
		synthesize: vi.fn((_text: string, _options?: VoiceSynthesisOptions) => emptyStream),
		stop: vi.fn(),
		dispose: vi.fn(),
		...overrides,
	};
}

describe("VoiceRuntime Web Speech-only routing", () => {
	it("uses only Web Speech and forwards an installed browser voice selection", async () => {
		const provider = makeProvider();
		const runtime = new VoiceRuntime({ enabled: true, createProvider: () => provider });

		await runtime.speak("Hello.", { language: "en", voiceId: "local-en", rate: 1.2 });

		expect(provider.synthesize).toHaveBeenCalledWith("Hello.", { language: "en", voiceId: "local-en", rate: 1.2 });
	});

	it("forwards a persisted af_heart selection without starting model or network behavior", async () => {
		const provider = makeProvider();
		const createProvider = vi.fn(() => provider);
		const runtime = new VoiceRuntime({ enabled: true, createProvider });

		await runtime.speak("Legacy selection.", { language: "en", voiceId: "af_heart" });

		expect(createProvider).toHaveBeenCalledTimes(1);
		expect(provider.synthesize).toHaveBeenCalledWith("Legacy selection.", { language: "en", voiceId: "af_heart" });
	});

	it("is inert when the node voice feature is disabled", async () => {
		const createProvider = vi.fn(() => makeProvider());
		const runtime = new VoiceRuntime({ enabled: false, createProvider });

		await runtime.enqueue("Hello.");

		expect(createProvider).not.toHaveBeenCalled();
	});

	it("reports browser speech initialization failures and retries on the next request", async () => {
		const failure = new Error("speech engine unavailable");
		const first = makeProvider({ init: vi.fn(() => Promise.reject(failure)) });
		const second = makeProvider();
		const createProvider = vi.fn().mockReturnValueOnce(first).mockReturnValueOnce(second);
		const listener = vi.fn();
		const runtime = new VoiceRuntime({ enabled: true, createProvider });
		runtime.onError(listener);

		await runtime.speak("First.");
		await runtime.speak("Second.");

		expect(listener).toHaveBeenCalledWith({ providerId: "web-speech", error: failure });
		expect(second.synthesize).toHaveBeenCalledWith("Second.", undefined);
	});
});

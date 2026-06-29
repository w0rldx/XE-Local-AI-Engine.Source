import { afterEach, describe, expect, it, type Mock, vi } from "vitest";

import type { VoiceCapabilities } from "./CapabilityDetector";
import { PlaybackQueue, type QueueAudioContext } from "./PlaybackQueue";
import type { AudioChunk, TtsProvider, TtsProviderId, VoiceSynthesisOptions } from "./TtsProvider";
import { findVoiceById, mockVoiceManifest, type VoiceManifest } from "./VoiceManifest";
import { createDefaultProvider, ModelNotAllowedError, selectProviderLadder, VoiceRuntime } from "./VoiceRuntime";

const emptyStream: AsyncIterable<AudioChunk> = {
	[Symbol.asyncIterator]: () => ({ next: () => Promise.resolve({ value: undefined, done: true }) }),
};

const throwingStream: AsyncIterable<AudioChunk> = {
	[Symbol.asyncIterator]: () => ({ next: () => Promise.reject(new Error("worker crash")) }),
};

// A synthesis stream whose chunks are pushed by the test, so a `stop()` can be interleaved between two chunks to
// reproduce the worker streaming PAST barge-in (the real Kokoro worker ignores `cancel`).
function makeControlledStream(): {
	readonly stream: AsyncIterable<AudioChunk>;
	push: (chunk: AudioChunk) => void;
	end: () => void;
} {
	const queue: AudioChunk[] = [];
	let done = false;
	let notify: (() => void) | undefined;
	const wake = (): void => {
		notify?.();
		notify = undefined;
	};

	const stream: AsyncIterable<AudioChunk> = {
		[Symbol.asyncIterator]: () => ({
			next: async () => {
				for (;;) {
					const next = queue.shift();
					if (next !== undefined) {
						return { value: next, done: false };
					}

					if (done) {
						return { value: undefined, done: true };
					}

					// biome-ignore lint/performance/noAwaitInLoops: the fake stream parks until the next chunk is pushed — sequential by design.
					await new Promise<void>((resolve) => {
						notify = resolve;
					});
				}
			},
		}),
	};

	return {
		stream,
		push: (chunk) => {
			queue.push(chunk);
			wake();
		},
		end: () => {
			done = true;
			wake();
		},
	};
}

const flushMacrotask = (): Promise<void> => new Promise((resolve) => setTimeout(resolve, 0));

interface MockProvider extends TtsProvider {
	readonly synthesizeSpy: Mock;
}

function makeProvider(
	id: TtsProviderId,
	opts: { producesPcm: boolean; initRejects?: boolean; synthThrows?: boolean },
): MockProvider {
	const synthesizeSpy = vi.fn((_text: string, _options?: VoiceSynthesisOptions) =>
		opts.synthThrows ? throwingStream : emptyStream,
	);
	return {
		id,
		producesPcm: opts.producesPcm,
		init: () => (opts.initRejects ? Promise.reject(new Error(`${id} init fail`)) : Promise.resolve()),
		synthesize: synthesizeSpy,
		stop: vi.fn(),
		dispose: vi.fn(),
		synthesizeSpy,
	};
}

function makeCapabilities(flags: { webgpu: boolean; wasm: boolean; webSpeech: boolean }): VoiceCapabilities {
	return { webgpu: flags.webgpu, wasm: flags.wasm, webSpeech: { available: flags.webSpeech, voices: [] } };
}

function makePlaybackQueue(): PlaybackQueue {
	const context: QueueAudioContext = {
		state: "running",
		currentTime: 0,
		destination: {},
		createBuffer: (_channels, length, sampleRate) => ({ duration: length / sampleRate, copyToChannel: () => undefined }),
		createBufferSource: () => ({
			buffer: null,
			onended: null,
			connect: () => undefined,
			disconnect: () => undefined,
			start: () => undefined,
			stop: () => undefined,
		}),
		resume: () => Promise.resolve(),
		suspend: () => Promise.resolve(),
		close: () => Promise.resolve(),
	};
	return new PlaybackQueue(() => context);
}

const emptyManifest: VoiceManifest = { enabled: true, models: [], voices: [], defaultVoiceId: "" };

describe("selectProviderLadder", () => {
	it("falls back to Kokoro WASM first when WebGPU is unavailable (English)", () => {
		const ladder = selectProviderLadder({
			language: "en",
			capabilities: makeCapabilities({ webgpu: false, wasm: true, webSpeech: true }),
			manifest: mockVoiceManifest,
		});

		expect(ladder).toEqual(["kokoro-wasm", "web-speech"]);
	});

	it("orders WebGPU → WASM → Web Speech for English on a fully capable browser", () => {
		const ladder = selectProviderLadder({
			language: "en",
			capabilities: makeCapabilities({ webgpu: true, wasm: true, webSpeech: true }),
			manifest: mockVoiceManifest,
		});

		expect(ladder).toEqual(["kokoro-webgpu", "kokoro-wasm", "web-speech"]);
	});

	it("routes non-English (German) answers straight to Web Speech (Kokoro has no German voice)", () => {
		const ladder = selectProviderLadder({
			language: "de",
			capabilities: makeCapabilities({ webgpu: true, wasm: true, webSpeech: true }),
			manifest: mockVoiceManifest,
		});

		expect(ladder).toEqual(["web-speech"]);
	});

	it("excludes Kokoro rungs when the manifest allows no model", () => {
		const ladder = selectProviderLadder({
			language: "en",
			capabilities: makeCapabilities({ webgpu: true, wasm: true, webSpeech: true }),
			manifest: emptyManifest,
		});

		expect(ladder).toEqual(["web-speech"]);
	});
});

describe("findVoiceById (selected-voice language resolution)", () => {
	it("returns the German voice's own language so chat routes to Web Speech (selected voice wins)", () => {
		expect(findVoiceById(mockVoiceManifest, "de_web_default")?.language).toBe("de");
	});

	it("returns an English voice's language", () => {
		expect(findVoiceById(mockVoiceManifest, "af_heart")?.language).toBe("en");
	});

	it("returns undefined for an unknown id or no id, so callers fall back to text detection", () => {
		expect(findVoiceById(mockVoiceManifest, "nope")).toBeUndefined();
		expect(findVoiceById(mockVoiceManifest, undefined)).toBeUndefined();
	});
});

describe("createDefaultProvider allow-list", () => {
	it("rejects loading a Kokoro model the manifest does not allow", () => {
		expect(() => createDefaultProvider("kokoro-webgpu", emptyManifest)).toThrow(ModelNotAllowedError);
	});
});

describe("VoiceRuntime fallback", () => {
	it("falls back WebGPU → WASM → Web Speech as each provider fails to initialize", async () => {
		const providers: Partial<Record<TtsProviderId, MockProvider>> = {
			"kokoro-webgpu": makeProvider("kokoro-webgpu", { producesPcm: true, initRejects: true }),
			"kokoro-wasm": makeProvider("kokoro-wasm", { producesPcm: true, initRejects: true }),
			"web-speech": makeProvider("web-speech", { producesPcm: false }),
		};
		const runtime = new VoiceRuntime({
			manifest: mockVoiceManifest,
			capabilities: makeCapabilities({ webgpu: true, wasm: true, webSpeech: true }),
			playbackQueue: makePlaybackQueue(),
			createProvider: (id) => providers[id] ?? makeProvider(id, { producesPcm: false }),
		});

		await runtime.speak("Hello there.", { language: "en" });

		expect(providers["web-speech"]?.synthesizeSpy).toHaveBeenCalledTimes(1);
		expect(runtime.lastError?.providerId).toBe("kokoro-wasm");
	});

	it("falls back to the next provider when synthesis fails mid-stream (worker crash)", async () => {
		const providers: Partial<Record<TtsProviderId, MockProvider>> = {
			"kokoro-webgpu": makeProvider("kokoro-webgpu", { producesPcm: true, synthThrows: true }),
			"web-speech": makeProvider("web-speech", { producesPcm: false }),
		};
		const runtime = new VoiceRuntime({
			manifest: mockVoiceManifest,
			capabilities: makeCapabilities({ webgpu: true, wasm: false, webSpeech: true }),
			playbackQueue: makePlaybackQueue(),
			createProvider: (id) => providers[id] ?? makeProvider(id, { producesPcm: false }),
		});

		await runtime.speak("Hello.", { language: "en" });

		expect(providers["kokoro-webgpu"]?.synthesizeSpy).toHaveBeenCalled();
		expect(providers["web-speech"]?.synthesizeSpy).toHaveBeenCalled();
		expect(runtime.lastError?.providerId).toBe("kokoro-webgpu");
	});

	it("routes English to Kokoro WebGPU", async () => {
		const providers: Partial<Record<TtsProviderId, MockProvider>> = {
			"kokoro-webgpu": makeProvider("kokoro-webgpu", { producesPcm: true }),
			"web-speech": makeProvider("web-speech", { producesPcm: false }),
		};
		const runtime = new VoiceRuntime({
			manifest: mockVoiceManifest,
			capabilities: makeCapabilities({ webgpu: true, wasm: true, webSpeech: true }),
			playbackQueue: makePlaybackQueue(),
			createProvider: (id) => providers[id] ?? makeProvider(id, { producesPcm: false }),
		});

		await runtime.speak("Hello.", { language: "en" });

		expect(providers["kokoro-webgpu"]?.synthesizeSpy).toHaveBeenCalled();
		expect(providers["web-speech"]?.synthesizeSpy).not.toHaveBeenCalled();
	});

	it("routes German to Web Speech, never Kokoro", async () => {
		const providers: Partial<Record<TtsProviderId, MockProvider>> = {
			"kokoro-webgpu": makeProvider("kokoro-webgpu", { producesPcm: true }),
			"web-speech": makeProvider("web-speech", { producesPcm: false }),
		};
		const runtime = new VoiceRuntime({
			manifest: mockVoiceManifest,
			capabilities: makeCapabilities({ webgpu: true, wasm: true, webSpeech: true }),
			playbackQueue: makePlaybackQueue(),
			createProvider: (id) => providers[id] ?? makeProvider(id, { producesPcm: false }),
		});

		await runtime.speak("Hallo Welt.", { language: "de" });

		expect(providers["web-speech"]?.synthesizeSpy).toHaveBeenCalled();
		expect(providers["kokoro-webgpu"]?.synthesizeSpy).not.toHaveBeenCalled();
	});

	it("drops chunks that arrive after stop() so audio does not survive barge-in", async () => {
		const controlled = makeControlledStream();
		const provider = makeProvider("kokoro-webgpu", { producesPcm: true });
		provider.synthesizeSpy.mockReturnValue(controlled.stream);

		// Count source nodes scheduled: while the context is running, each enqueued chunk creates+starts one.
		let scheduled = 0;
		const context: QueueAudioContext = {
			state: "running",
			currentTime: 0,
			destination: {},
			createBuffer: (_channels, length, sampleRate) => ({ duration: length / sampleRate, copyToChannel: () => undefined }),
			createBufferSource: () => {
				scheduled++;
				return {
					buffer: null,
					onended: null,
					connect: () => undefined,
					disconnect: () => undefined,
					start: () => undefined,
					stop: () => undefined,
				};
			},
			resume: () => Promise.resolve(),
			suspend: () => Promise.resolve(),
			close: () => Promise.resolve(),
		};

		const runtime = new VoiceRuntime({
			manifest: mockVoiceManifest,
			capabilities: makeCapabilities({ webgpu: true, wasm: false, webSpeech: true }),
			playbackQueue: new PlaybackQueue(() => context),
			createProvider: (id) => (id === "kokoro-webgpu" ? provider : makeProvider(id, { producesPcm: false })),
		});

		const chunk: AudioChunk = { pcm: new Float32Array(8), sampleRate: 24000 };
		const speaking = runtime.speak("Hello there.", { language: "en" });

		controlled.push(chunk);
		await flushMacrotask();
		expect(scheduled).toBe(1);

		// Barge-in mid-stream, then the worker streams one more chunk — it must NOT schedule a new source node.
		runtime.stop();
		controlled.push(chunk);
		controlled.end();
		await speaking;

		expect(scheduled).toBe(1);
		expect(provider.stop).toHaveBeenCalled();
	});

	it("is inert when the manifest disables voice", async () => {
		const createProvider = vi.fn((id: TtsProviderId) => makeProvider(id, { producesPcm: false }));
		const runtime = new VoiceRuntime({
			manifest: { ...mockVoiceManifest, enabled: false },
			capabilities: makeCapabilities({ webgpu: true, wasm: true, webSpeech: true }),
			playbackQueue: makePlaybackQueue(),
			createProvider,
		});

		await runtime.enqueue("Hello.", { language: "en" });

		expect(createProvider).not.toHaveBeenCalled();
	});
});

describe("VoiceRuntime mock-manifest guard", () => {
	const deps = () => ({
		manifest: mockVoiceManifest,
		capabilities: makeCapabilities({ webgpu: true, wasm: true, webSpeech: true }),
		playbackQueue: makePlaybackQueue(),
		createProvider: (id: TtsProviderId) => makeProvider(id, { producesPcm: false }),
	});

	afterEach(() => {
		vi.unstubAllEnvs();
	});

	it("refuses the mock manifest in a production build", () => {
		vi.stubEnv("DEV", false);

		expect(() => new VoiceRuntime(deps())).toThrow(/mock voice manifest/i);
	});

	it("still accepts the mock manifest in a dev/test build", () => {
		vi.stubEnv("DEV", true);

		expect(() => new VoiceRuntime(deps())).not.toThrow();
	});

	it("accepts a real (unbranded) manifest in a production build", () => {
		vi.stubEnv("DEV", false);
		const realManifest: VoiceManifest = { enabled: true, models: [], voices: [], defaultVoiceId: "" };

		expect(() => new VoiceRuntime({ ...deps(), manifest: realManifest })).not.toThrow();
	});
});

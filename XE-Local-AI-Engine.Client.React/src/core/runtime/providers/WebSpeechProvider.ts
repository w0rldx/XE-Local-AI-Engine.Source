// Web Speech API provider — the zero-download fallback floor (plan §5, §7.2).
//
// This is a self-playing provider (`producesPcm: false`): it renders audio through the OS speech engine rather than
// emitting PCM, so `synthesize` queues an utterance and yields nothing. It is also the German (and any non-English)
// path, since Kokoro-82M ships no German voice — for `de` content it picks an on-device (`localService`) OS voice.

import type { AudioChunk, TtsProvider, TtsProviderId, VoiceSynthesisOptions } from "../TtsProvider";

/** Minimal speech-engine surface; the real `speechSynthesis` and test fakes both satisfy it. */
export interface SpeechSynthesisLike {
	getVoices(): SpeechSynthesisVoice[];
	speak(utterance: SpeechSynthesisUtterance): void;
	cancel(): void;
}

export type UtteranceFactory = (text: string) => SpeechSynthesisUtterance;

export interface WebSpeechProviderOptions {
	readonly synthesis?: SpeechSynthesisLike;
	readonly createUtterance?: UtteranceFactory;
}

function resolveSynthesis(injected?: SpeechSynthesisLike): SpeechSynthesisLike | undefined {
	return injected ?? globalThis.speechSynthesis;
}

const defaultUtteranceFactory: UtteranceFactory = (text) => new SpeechSynthesisUtterance(text);

// A self-playing provider emits no PCM, so its synthesis "stream" is empty. Built as a plain async-iterable (not an
// empty generator, which would trip the useYield lint) that immediately reports done.
const emptyAudioStream: AsyncIterable<AudioChunk> = {
	[Symbol.asyncIterator]: () => ({
		next: () => Promise.resolve({ value: undefined, done: true }),
	}),
};

export class WebSpeechProvider implements TtsProvider {
	readonly id: TtsProviderId = "web-speech";
	readonly producesPcm = false;

	private readonly synthesis: SpeechSynthesisLike | undefined;
	private readonly createUtterance: UtteranceFactory;

	constructor(options?: WebSpeechProviderOptions) {
		this.synthesis = resolveSynthesis(options?.synthesis);
		this.createUtterance = options?.createUtterance ?? defaultUtteranceFactory;
	}

	/** Verifies the speech engine is present; rejects otherwise so this rung is skipped on unsupported browsers. */
	init(): Promise<void> {
		if (!this.synthesis) {
			return Promise.reject(new Error("Web Speech API is not available"));
		}

		return Promise.resolve();
	}

	/**
	 * Queues one utterance for `text` and returns an empty stream. The OS speech queue preserves order across
	 * successive calls, so sentence-buffered enqueues play back in sequence. Emits no PCM (self-playing).
	 */
	synthesize(text: string, options?: VoiceSynthesisOptions): AsyncIterable<AudioChunk> {
		const synthesis = this.synthesis;
		if (!synthesis) {
			throw new Error("Web Speech API is not available");
		}

		const utterance = this.createUtterance(text);
		const voice = this.pickVoice(synthesis, options?.language);
		if (voice) {
			utterance.voice = voice;
			utterance.lang = voice.lang;
		} else if (options?.language) {
			utterance.lang = options.language;
		}

		if (options?.rate !== undefined) {
			utterance.rate = options.rate;
		}

		synthesis.speak(utterance);
		return emptyAudioStream;
	}

	/** Barge-in: clears the OS speech queue and stops the current utterance. */
	stop(): void {
		this.synthesis?.cancel();
	}

	dispose(): void {
		this.synthesis?.cancel();
	}

	// Picks an OS voice whose language matches the requested code (prefix match), preferring on-device voices so the
	// fallback stays offline-capable. Returns undefined when no language match exists (engine picks its default).
	private pickVoice(synthesis: SpeechSynthesisLike, language?: string): SpeechSynthesisVoice | undefined {
		if (!language) {
			return undefined;
		}

		const prefix = language.toLowerCase();
		const matches = synthesis.getVoices().filter((voice) => voice.lang.toLowerCase().startsWith(prefix));
		if (matches.length === 0) {
			return undefined;
		}

		return matches.find((voice) => voice.localService) ?? matches[0];
	}
}

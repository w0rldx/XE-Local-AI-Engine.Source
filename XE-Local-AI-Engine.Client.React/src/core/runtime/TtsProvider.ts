// The TTS provider abstraction — the single contract every speech backend implements.
//
// Two provider families exist:
//  - PCM providers (`producesPcm: true`, e.g. Kokoro) yield raw audio chunks the `PlaybackQueue` schedules.
//  - Self-playing providers (`producesPcm: false`, e.g. Web Speech) render audio through the OS and yield nothing.
// `VoiceRuntime` branches on `producesPcm` so a uniform interface covers both without leaking provider details.

import type { VoiceLanguageCode } from "./VoiceManifest";

/** Stable provider identifiers, ordered conceptually best→floor down the fallback ladder. */
export type TtsProviderId = "kokoro-webgpu" | "kokoro-wasm" | "web-speech" | "remote";

/** A single decoded audio chunk emitted by a PCM provider. Kokoro yields 24 kHz mono `Float32Array` PCM. */
export interface AudioChunk {
	readonly pcm: Float32Array;
	readonly sampleRate: number;
}

/** Per-synthesis options. `voiceId` selects the speaker, `rate` the speaking speed, `language` the routing hint. */
export interface VoiceSynthesisOptions {
	readonly voiceId?: string;
	readonly rate?: number;
	readonly language?: VoiceLanguageCode;
}

export interface TtsProvider {
	readonly id: TtsProviderId;
	/** True when `synthesize` yields `AudioChunk`s for the `PlaybackQueue`; false when the provider plays audio itself. */
	readonly producesPcm: boolean;
	/** Loads weights / acquires the speech engine. Rejects on failure so `VoiceRuntime` can fall back. */
	init(): Promise<void>;
	/** Streams synthesized audio. PCM providers yield chunks; self-playing providers speak and yield nothing. */
	synthesize(text: string, options?: VoiceSynthesisOptions): AsyncIterable<AudioChunk>;
	/** Barge-in: halts any in-flight utterance immediately (self-playing providers cancel their queued speech). */
	stop(): void;
	/** Releases the worker / engine handle. Idempotent. */
	dispose(): void;
}

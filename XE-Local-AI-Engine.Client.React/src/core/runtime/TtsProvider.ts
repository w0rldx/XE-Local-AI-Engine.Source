// Contract for the browser-owned Web Speech provider.

export type TtsProviderId = "web-speech";
export type VoiceLanguageCode = string;

/** Retained as the empty-stream element type so the provider contract stays an async iterable. */
export interface AudioChunk {
	readonly pcm: Float32Array;
	readonly sampleRate: number;
}

export interface VoiceSynthesisOptions {
	readonly voiceId?: string;
	readonly rate?: number;
	readonly language?: VoiceLanguageCode;
}

export interface TtsProvider {
	readonly id: TtsProviderId;
	readonly producesPcm: false;
	init(): Promise<void>;
	synthesize(text: string, options?: VoiceSynthesisOptions): AsyncIterable<AudioChunk>;
	stop(): void;
	dispose(): void;
}

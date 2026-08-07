// Web Speech-only voice runtime. The browser speech engine owns playback; there are no model downloads, workers,
// neural inference providers, or AudioContext resources to initialise.

import { WebSpeechProvider } from "./providers/WebSpeechProvider";
import type { TtsProvider, TtsProviderId, VoiceSynthesisOptions } from "./TtsProvider";

export type ProviderFactory = () => TtsProvider;

export interface VoiceRuntimeDeps {
	readonly enabled: boolean;
	/** Injectable for tests; defaults to the browser's Web Speech provider. */
	readonly createProvider?: ProviderFactory;
}

type ErrorListener = (error: VoiceRuntimeError) => void;

export interface VoiceRuntimeError {
	readonly providerId: TtsProviderId;
	readonly error: Error;
}

export class VoiceRuntime {
	private readonly enabled: boolean;
	private readonly createProvider: ProviderFactory;
	private readonly errorListeners = new Set<ErrorListener>();
	private provider: TtsProvider | undefined;
	private lastErrorValue: VoiceRuntimeError | undefined;

	constructor(deps: VoiceRuntimeDeps) {
		this.enabled = deps.enabled;
		this.createProvider = deps.createProvider ?? (() => new WebSpeechProvider());
	}

	get lastError(): VoiceRuntimeError | undefined {
		return this.lastErrorValue;
	}

	onError(listener: ErrorListener): () => void {
		this.errorListeners.add(listener);
		return () => this.errorListeners.delete(listener);
	}

	async speak(text: string, options?: VoiceSynthesisOptions): Promise<void> {
		this.stop();
		await this.enqueue(text, options);
	}

	async enqueue(text: string, options?: VoiceSynthesisOptions): Promise<void> {
		if (!this.enabled || text.trim().length === 0) {
			return;
		}

		try {
			const provider = await this.ensureProvider();
			for await (const _chunk of provider.synthesize(text, options)) {
				// Web Speech renders internally and intentionally yields no PCM chunks.
			}
		} catch (error) {
			this.recordFailure(error);
		}
	}

	stop(): void {
		this.provider?.stop();
	}

	dispose(): void {
		this.provider?.dispose();
		this.provider = undefined;
	}

	private async ensureProvider(): Promise<TtsProvider> {
		if (this.provider) {
			return this.provider;
		}

		const provider = this.createProvider();
		await provider.init();
		this.provider = provider;
		return provider;
	}

	private recordFailure(error: unknown): void {
		this.provider?.dispose();
		this.provider = undefined;
		const runtimeError: VoiceRuntimeError = {
			providerId: "web-speech",
			error: error instanceof Error ? error : new Error(String(error)),
		};
		this.lastErrorValue = runtimeError;
		for (const listener of this.errorListeners) {
			listener(runtimeError);
		}
	}
}

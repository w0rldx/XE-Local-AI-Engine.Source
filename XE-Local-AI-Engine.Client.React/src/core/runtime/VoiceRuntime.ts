// VoiceRuntime — selects a TTS provider by capability ∩ manifest ∩ answer language, and drives playback with a
// fall-DOWN-the-ladder recovery so a provider failure degrades instead of hanging.
//
// Routing: English answers go to Kokoro (WebGPU → WASM) then Web Speech; non-English
// answers (notably German — Kokoro ships no German voice) route straight to Web Speech, which has OS voices. Web
// Speech is always the floor. The provider factory is injected so the runtime is unit-testable without real workers,
// WebGPU, or audio. The manifest is supplied by the caller (the wiring layer injects the real one; the runtime uses the mock by default).

import { detectVoiceCapabilities, type VoiceCapabilities } from "./CapabilityDetector";
import { PlaybackQueue } from "./PlaybackQueue";
import { KokoroProvider } from "./providers/KokoroProvider";
import { WebSpeechProvider } from "./providers/WebSpeechProvider";
import type { AudioChunk, TtsProvider, TtsProviderId, VoiceSynthesisOptions } from "./TtsProvider";
import {
	findAllowedModel,
	mockVoiceManifest,
	type VoiceLanguageCode,
	type VoiceManifest,
} from "./VoiceManifest";

/** Thrown when a provider is asked to load a model the manifest does not allow (defence in depth over the ladder). */
export class ModelNotAllowedError extends Error {
	constructor(readonly providerId: TtsProviderId) {
		super(`No manifest-allowed model for provider "${providerId}"`);
		this.name = "ModelNotAllowedError";
	}
}

export interface ProviderLadderInputs {
	readonly language: VoiceLanguageCode;
	readonly capabilities: VoiceCapabilities;
	readonly manifest: VoiceManifest;
}

/**
 * Computes the ordered provider fallback ladder for an answer. Pure + exported so the routing rules are directly
 * testable. English → Kokoro WebGPU → Kokoro WASM → Web Speech (each rung gated by capability + an allowed model);
 * non-English → Web Speech only. Web Speech is appended as the floor whenever it is available.
 */
export function selectProviderLadder({ language, capabilities, manifest }: ProviderLadderInputs): TtsProviderId[] {
	const ladder: TtsProviderId[] = [];
	const isEnglish = language.toLowerCase().startsWith("en");

	if (isEnglish) {
		if (capabilities.webgpu && findAllowedModel(manifest, "fp32")) {
			ladder.push("kokoro-webgpu");
		}

		if (capabilities.wasm && findAllowedModel(manifest, "q8")) {
			ladder.push("kokoro-wasm");
		}
	}

	if (capabilities.webSpeech.available) {
		ladder.push("web-speech");
	}

	return ladder;
}

/** Builds a concrete provider for an id, enforcing the manifest allow-list for Kokoro rungs. */
export function createDefaultProvider(id: TtsProviderId, manifest: VoiceManifest): TtsProvider {
	if (id === "kokoro-webgpu") {
		const model = findAllowedModel(manifest, "fp32");
		if (!model) {
			throw new ModelNotAllowedError(id);
		}

		return new KokoroProvider({ device: "webgpu", modelId: model.id, dtype: "fp32" });
	}

	if (id === "kokoro-wasm") {
		const model = findAllowedModel(manifest, "q8");
		if (!model) {
			throw new ModelNotAllowedError(id);
		}

		return new KokoroProvider({ device: "wasm", modelId: model.id, dtype: "q8" });
	}

	if (id === "web-speech") {
		return new WebSpeechProvider();
	}

	// Remote TTS is a deferred ladder rung — not built yet.
	throw new Error(`Provider "${id}" is not available in milestone 1`);
}

export type ProviderFactory = (id: TtsProviderId) => TtsProvider;

export interface VoiceRuntimeDeps {
	readonly manifest: VoiceManifest;
	readonly capabilities: VoiceCapabilities;
	readonly playbackQueue: PlaybackQueue;
	/** Injectable for tests; defaults to building real providers via `createDefaultProvider`. */
	readonly createProvider?: ProviderFactory;
}

type ErrorListener = (error: VoiceRuntimeError) => void;

export interface VoiceRuntimeError {
	readonly providerId: TtsProviderId;
	readonly error: Error;
}

interface ActiveProvider {
	readonly provider: TtsProvider;
	readonly providerId: TtsProviderId;
	readonly language: VoiceLanguageCode;
}

const DEFAULT_LANGUAGE: VoiceLanguageCode = "en";

export class VoiceRuntime {
	private readonly manifest: VoiceManifest;
	private readonly capabilities: VoiceCapabilities;
	private readonly playbackQueue: PlaybackQueue;
	private readonly createProvider: ProviderFactory;
	private readonly errorListeners = new Set<ErrorListener>();

	private active: ActiveProvider | undefined;
	// Providers that have failed during the CURRENT turn; skipped until `stop()`/`speak()` resets the turn.
	private readonly failedThisTurn = new Set<TtsProviderId>();
	private lastErrorValue: VoiceRuntimeError | undefined;
	// Monotonic turn token, bumped on every `stop()`. A synthesis loop captures the token it started under and drops any
	// chunk it pulls once the token has advanced — otherwise the worker keeps streaming and `route` would schedule fresh
	// source nodes into the running queue AFTER barge-in, so audio would survive Stop (the worker ignores `cancel`).
	private turn = 0;

	constructor(deps: VoiceRuntimeDeps) {
		this.manifest = deps.manifest;
		this.capabilities = deps.capabilities;
		this.playbackQueue = deps.playbackQueue;
		this.createProvider = deps.createProvider ?? ((id) => createDefaultProvider(id, deps.manifest));
	}

	/** The most recent provider error, or undefined. */
	get lastError(): VoiceRuntimeError | undefined {
		return this.lastErrorValue;
	}

	/** Subscribes to provider errors; returns an unsubscribe function. */
	onError(listener: ErrorListener): () => void {
		this.errorListeners.add(listener);
		return () => {
			this.errorListeners.delete(listener);
		};
	}

	/** Barge-in then speak `text` as a fresh turn (stops any current playback, resets the fallback ladder). */
	async speak(text: string, options?: VoiceSynthesisOptions): Promise<void> {
		this.stop();
		await this.enqueue(text, options);
	}

	/** Synthesizes one sentence/clause for the current turn, routing through the capability+language ladder. */
	async enqueue(text: string, options?: VoiceSynthesisOptions): Promise<void> {
		if (!this.manifest.enabled || text.trim().length === 0) {
			return;
		}

		const language = options?.language ?? DEFAULT_LANGUAGE;
		await this.synthesizeWithFallback(text, language, options);
	}

	/** Barge-in: halts playback + the active provider and resets the per-turn failure set. Idempotent / no-op when idle. */
	stop(): void {
		this.turn++;
		this.playbackQueue.stop();
		this.active?.provider.stop();
		this.failedThisTurn.clear();
	}

	/** Pauses playback (resumable) without tearing down the AudioContext. */
	async pause(): Promise<void> {
		await this.playbackQueue.suspend();
	}

	/** Resumes playback — also satisfies the autoplay-policy gesture unlock on the owned AudioContext. */
	async resume(): Promise<void> {
		await this.playbackQueue.resume();
	}

	/** Releases the active provider. The AudioContext is owned by the root provider, so it is not closed here. */
	dispose(): void {
		this.active?.provider.dispose();
		this.active = undefined;
	}

	private async synthesizeWithFallback(
		text: string,
		language: VoiceLanguageCode,
		options: VoiceSynthesisOptions | undefined,
	): Promise<void> {
		const turn = this.turn;
		const ladder = selectProviderLadder({ language, capabilities: this.capabilities, manifest: this.manifest });
		const candidates = ladder.filter((id) => !this.failedThisTurn.has(id));

		for (const id of candidates) {
			if (turn !== this.turn) {
				return;
			}

			try {
				// biome-ignore lint/performance/noAwaitInLoops: the fallback ladder is inherently sequential — only try the next provider after the current one fails.
				const provider = await this.ensureProvider(id, language);
				await this.route(provider, text, { voiceId: options?.voiceId, rate: options?.rate, language }, turn);
				return;
			} catch (error) {
				this.recordFailure(id, error);
			}
		}
	}

	// Returns the active provider when it already matches (provider reuse within a turn), otherwise disposes the old
	// one and initializes a freshly built provider. A thrown init propagates so the caller falls to the next rung.
	private async ensureProvider(id: TtsProviderId, language: VoiceLanguageCode): Promise<TtsProvider> {
		if (this.active && this.active.providerId === id && this.active.language === language) {
			return this.active.provider;
		}

		if (this.active) {
			this.active.provider.dispose();
			this.active = undefined;
		}

		const provider = this.createProvider(id);
		await provider.init();
		this.active = { provider, providerId: id, language };
		return provider;
	}

	// Drains the provider's synthesis stream. PCM providers yield chunks routed to the playback queue; self-playing
	// providers (Web Speech) render audio internally and yield nothing, so the loop simply completes. If `stop()` ran
	// while a chunk was in flight, the captured `turn` no longer matches: stop the provider and drop the rest so no
	// post-barge-in chunk reaches the running queue (where it would schedule a fresh source node and keep playing).
	private async route(provider: TtsProvider, text: string, options: VoiceSynthesisOptions, turn: number): Promise<void> {
		for await (const chunk of provider.synthesize(text, options) as AsyncIterable<AudioChunk>) {
			if (turn !== this.turn) {
				provider.stop();
				return;
			}

			this.playbackQueue.enqueue(chunk);
		}
	}

	private recordFailure(id: TtsProviderId, error: unknown): void {
		this.failedThisTurn.add(id);

		if (this.active?.providerId === id) {
			this.active.provider.dispose();
			this.active = undefined;
		}

		const normalized = error instanceof Error ? error : new Error(String(error));
		const runtimeError: VoiceRuntimeError = { providerId: id, error: normalized };
		this.lastErrorValue = runtimeError;
		for (const listener of [...this.errorListeners]) {
			listener(runtimeError);
		}
	}
}

/**
 * Convenience factory: probes capabilities, builds a `PlaybackQueue`, and returns a ready `VoiceRuntime` using the
 * mock manifest. The wiring layer calls this (or constructs `VoiceRuntime` directly) with the backend manifest in
 * place of `mockVoiceManifest`.
 */
export async function createVoiceRuntime(manifest: VoiceManifest = mockVoiceManifest): Promise<VoiceRuntime> {
	const capabilities = await detectVoiceCapabilities();
	return new VoiceRuntime({ manifest, capabilities, playbackQueue: new PlaybackQueue() });
}

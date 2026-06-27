// Runtime capability probing for the voice runtime.
//
// The fallback ladder is decided by what the *browser* can actually do, probed once at app init and cached — never a
// static "this browser supports X" table. WebGPU is probed via the real adapter→device handshake
// (an adapter request can resolve null, and device creation can throw / be lost), so detection must tolerate every
// failure mode without throwing. Web Speech voices load asynchronously, so detection awaits `voiceschanged` once.

// Minimal structural types for the WebGPU surface — the standard DOM lib does not declare `navigator.gpu`, and we
// avoid pulling @webgpu/types for a one-shot probe. Only the members the probe touches are modelled.
interface MinimalGpuDevice {
	readonly lost: Promise<unknown>;
}

interface MinimalGpuAdapter {
	requestDevice(): Promise<MinimalGpuDevice>;
}

interface MinimalGpu {
	requestAdapter(): Promise<MinimalGpuAdapter | null>;
}

/** A single OS / browser speech voice usable by the Web Speech fallback. */
export interface WebSpeechVoiceInfo {
	readonly voiceId: string;
	readonly name: string;
	/** BCP-47 language tag of the voice, e.g. "en-US", "de-DE". */
	readonly lang: string;
	/** True when the voice runs on-device (no network round-trip) — preferred for an offline-capable fallback. */
	readonly localService: boolean;
}

export interface WebSpeechCapability {
	readonly available: boolean;
	readonly voices: readonly WebSpeechVoiceInfo[];
}

/** Immutable snapshot of what the current browser can do; produced once and cached for the session. */
export interface VoiceCapabilities {
	readonly webgpu: boolean;
	readonly wasm: boolean;
	readonly webSpeech: WebSpeechCapability;
}

function getNavigatorGpu(): MinimalGpu | undefined {
	if (typeof navigator === "undefined") {
		return undefined;
	}

	return (navigator as Navigator & { gpu?: MinimalGpu }).gpu;
}

/**
 * Probes WebGPU end-to-end: `navigator.gpu` → `requestAdapter()` (may be null) → `requestDevice()` (may throw or be
 * lost). Any failure resolves `false` rather than throwing, so the caller can degrade to the next rung.
 */
export async function detectWebGpu(): Promise<boolean> {
	const gpu = getNavigatorGpu();
	if (!gpu) {
		return false;
	}

	try {
		const adapter = await gpu.requestAdapter();
		if (!adapter) {
			return false;
		}

		const device = await adapter.requestDevice();
		// A device can be lost asynchronously (driver reset, tab backgrounded). Swallow the rejection so an unhandled
		// promise never surfaces; the cache is not invalidated on loss (dynamic re-probe is deferred to a later release).
		device.lost.catch(() => undefined);
		return true;
	} catch {
		return false;
	}
}

/** WASM is the universal neural-inference path; detected by the presence of the `WebAssembly` API. */
export function detectWasm(): boolean {
	return typeof WebAssembly === "object" && typeof WebAssembly.instantiate === "function";
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

/**
 * Detects the Web Speech API. Voices populate asynchronously, so when the initial list is empty we wait for one
 * `voiceschanged` event (bounded by `voicesTimeoutMs`) before reading the list. The returned capability exposes every
 * voice with its `lang` + `localService` flag so the Web Speech provider can pick an on-device language match.
 */
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

/** Probes all capabilities concurrently and returns an immutable snapshot. */
export async function probeVoiceCapabilities(voicesTimeoutMs?: number): Promise<VoiceCapabilities> {
	const [webgpu, webSpeech] = await Promise.all([detectWebGpu(), detectWebSpeech(voicesTimeoutMs)]);
	return { webgpu, wasm: detectWasm(), webSpeech };
}

let cachedCapabilities: Promise<VoiceCapabilities> | undefined;

/**
 * Probes once at app init and caches the result for the session. Subsequent calls return the same
 * snapshot. A `voiceschanged` listener is attached so late-loading voices are observable to callers that re-read, but
 * the cached snapshot itself is intentionally NOT invalidated for now (dynamic re-probe deferred to a later release).
 */
export function detectVoiceCapabilities(voicesTimeoutMs?: number): Promise<VoiceCapabilities> {
	cachedCapabilities ??= probeVoiceCapabilities(voicesTimeoutMs);
	return cachedCapabilities;
}

/** Clears the cached snapshot. Test-only seam so each case probes a freshly-mocked environment. */
export function resetVoiceCapabilitiesCache(): void {
	cachedCapabilities = undefined;
}

// EXPERIMENTAL / DEV-ONLY: The entire src/features/voice/ module is operator-gated to developer mode.
// Components and runtime code in this directory must NEVER be exposed outside of the developerMode +
// manifest.enabled double-gate (see lines 36-38 below and VoiceRuntime.ts:134). This is an intentional
// operator decision — voice TTS is client-side experimental and not ready for general availability.
//
// Client AI Runtime provider — owns the ONE long-lived voice runtime + AudioContext for the whole app session.
// Mounted at the app root so it survives route + conversation changes. It is
// deliberately NOT a generic feature registry: it owns exactly the VoiceRuntime, its
// PlaybackQueue (the single AudioContext), and the AudioContext autoplay-gesture lifecycle.
//
// The AudioContext starts suspended (browser autoplay policy); a one-shot global pointer/keydown listener resumes it
// on the first user gesture so subsequent enqueues actually play (enqueues before resume buffer, never throw). The
// runtime is built only when voice is dev-gated ON, the manifest enables it, and the browser exposes an AudioContext.

import { type ReactNode, useCallback, useEffect, useMemo, useRef, useState } from "react";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { detectVoiceCapabilities, type VoiceCapabilities } from "@/core/runtime/CapabilityDetector";
import { ModelCache, type ModelDownloadError, type ModelDownloadProgress } from "@/core/runtime/ModelCache";
import { PlaybackQueue } from "@/core/runtime/PlaybackQueue";
import { sanitizeForSpeech } from "@/core/runtime/SentenceBuffer";
import { findVoiceById } from "@/core/runtime/VoiceManifest";
import { VoiceRuntime, type VoiceRuntimeError } from "@/core/runtime/VoiceRuntime";
import { detectAnswerLanguage } from "@/features/voice/DetectAnswerLanguage";
import { toSpeakableText } from "@/features/voice/SpeakableText";
import { useVoiceManifest } from "@/features/voice/useVoiceManifest";
import { useVoicePreferencesStore } from "@/features/voice/VoicePreferencesStore";
import { voicePreviewSample } from "@/features/voice/VoicePreviewSample";
import { VoiceRuntimeContext, type VoiceRuntimeContextValue } from "@/features/voice/VoiceRuntimeContext";
import { resolveOsVoiceLanguage } from "@/features/voice/WebSpeechVoiceCatalog";

function hasAudioContext(): boolean {
	return typeof AudioContext !== "undefined";
}

interface RuntimeBundle {
	readonly runtime: VoiceRuntime;
	readonly playbackQueue: PlaybackQueue;
}

export function ClientAiRuntimeProvider({ children }: { readonly children: ReactNode }) {
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const { manifest } = useVoiceManifest({ enabled: developerMode });

	const enabled = developerMode && manifest?.enabled === true;

	const [capabilities, setCapabilities] = useState<VoiceCapabilities | undefined>();
	const [audioSuspended, setAudioSuspended] = useState(true);
	const [downloadProgress, setDownloadProgress] = useState<ModelDownloadProgress | undefined>();
	const [downloadError, setDownloadError] = useState<ModelDownloadError | undefined>();
	const [lastError, setLastError] = useState<VoiceRuntimeError | undefined>();
	const [playingMessageId, setPlayingMessageId] = useState<string | undefined>();

	const bundleRef = useRef<RuntimeBundle | undefined>(undefined);
	const [bundle, setBundle] = useState<RuntimeBundle | undefined>();

	// The shared ModelCache lives for the whole session so its download progress/error events drive the notice UI
	// regardless of which provider/runtime instance is active. Built once, lazily, only in a browser environment.
	const modelCacheRef = useRef<ModelCache | undefined>(undefined);
	if (modelCacheRef.current === undefined && typeof window !== "undefined") {
		modelCacheRef.current = new ModelCache();
	}

	// Probe capabilities once when voice first becomes eligible (cached for the session by the detector).
	useEffect(() => {
		if (!enabled || capabilities) {
			return;
		}

		let cancelled = false;
		detectVoiceCapabilities()
			.then((probed) => {
				if (!cancelled) {
					setCapabilities(probed);
				}
			})
			.catch(() => undefined);

		return () => {
			cancelled = true;
		};
	}, [enabled, capabilities]);

	// Build the runtime + its single PlaybackQueue once all preconditions are met; tear down on disable/unmount.
	useEffect(() => {
		if (!enabled || !manifest || !capabilities || !hasAudioContext() || bundleRef.current) {
			return;
		}

		const playbackQueue = new PlaybackQueue();
		const runtime = new VoiceRuntime({ manifest, capabilities, playbackQueue });
		const created: RuntimeBundle = { runtime, playbackQueue };
		bundleRef.current = created;
		setBundle(created);

		const unsubscribe = runtime.onError(setLastError);

		return () => {
			unsubscribe();
			runtime.dispose();
			playbackQueue.close().catch(() => undefined);
			bundleRef.current = undefined;
			setBundle(undefined);
		};
	}, [enabled, manifest, capabilities]);

	// Subscribe the session ModelCache to feed the download-progress/error notice.
	useEffect(() => {
		const modelCache = modelCacheRef.current;
		if (!modelCache) {
			return;
		}

		const offProgress = modelCache.onProgress.on(setDownloadProgress);
		const offError = modelCache.onError.on(setDownloadError);
		return () => {
			offProgress();
			offError();
		};
	}, []);

	const resumeAudio = useCallback(async (): Promise<void> => {
		const queue = bundleRef.current?.playbackQueue;
		if (!queue) {
			return;
		}

		await queue.resume();
		setAudioSuspended(!queue.isRunning);
	}, []);

	// One-shot global gesture listener: unlock the AudioContext on the first user pointer/keydown so later enqueues
	// play instead of buffering forever (autoplay policy). Re-armed whenever a fresh runtime mounts.
	useEffect(() => {
		if (!bundle) {
			return;
		}

		const unlock = (): void => {
			resumeAudio().catch(() => undefined);
		};

		window.addEventListener("pointerdown", unlock, { once: true });
		window.addEventListener("keydown", unlock, { once: true });
		return () => {
			window.removeEventListener("pointerdown", unlock);
			window.removeEventListener("keydown", unlock);
		};
	}, [bundle, resumeAudio]);

	const dismissDownloadNotice = useCallback(() => {
		setDownloadProgress(undefined);
		setDownloadError(undefined);
	}, []);

	const stopPlayback = useCallback(() => {
		bundleRef.current?.runtime.stop();
		setPlayingMessageId(undefined);
	}, []);

	const playMessage = useCallback(
		async (messageId: string, text: string): Promise<void> => {
			const runtime = bundleRef.current?.runtime;
			if (!runtime) {
				return;
			}

			const sanitized = sanitizeForSpeech(toSpeakableText(text));
			if (sanitized.length === 0) {
				return;
			}

			const prefs = useVoicePreferencesStore.getState();
			const voiceId = prefs.voiceProfile || manifest?.defaultVoiceId || undefined;
			// "Selected voice always wins": the manual Play button routes by the SELECTED voice's OWN language so
			// it matches the node-settings preview exactly (the caller no longer guesses a language from the answer).
			// A voiceId that resolves to no manifest entry may still be a real OS voice picked from the full system
			// catalog (WebSpeechVoiceCatalog) — resolve its language there before falling back to detectAnswerLanguage,
			// which only applies when no voice resolves at all (no selection AND no manifest default).
			const selectedVoice = manifest ? findVoiceById(manifest, voiceId) : undefined;
			const osVoiceLanguage = selectedVoice || !voiceId ? undefined : resolveOsVoiceLanguage(voiceId);
			const language = selectedVoice?.language ?? osVoiceLanguage ?? detectAnswerLanguage(sanitized);
			setPlayingMessageId(messageId);
			await resumeAudio();
			await runtime.speak(sanitized, { language, voiceId, rate: prefs.speakingRate });
		},
		[manifest, resumeAudio],
	);

	const previewVoice = useCallback(
		async (voiceId: string): Promise<void> => {
			const runtime = bundleRef.current?.runtime;
			if (!runtime || !voiceId) {
				return;
			}

			// Speak the sample in the voice's OWN language (drives the en→Kokoro / de→Web-Speech routing correctly) at
			// the user's current speaking rate, so the audition matches what they'd actually hear. Independent of the
			// per-user enable/autoplay toggles — previewing is exactly how the user decides whether to turn voice on.
			// A voiceId outside the manifest may be an OS voice picked from the full system catalog — resolve its
			// language there so previewing an arbitrary OS voice speaks that voice's own language, not English.
			const voice = manifest ? findVoiceById(manifest, voiceId) : undefined;
			const language = voice?.language ?? resolveOsVoiceLanguage(voiceId) ?? "en";
			const prefs = useVoicePreferencesStore.getState();
			await resumeAudio();
			await runtime.speak(voicePreviewSample(language), { language, voiceId, rate: prefs.speakingRate });
		},
		[manifest, resumeAudio],
	);

	const value = useMemo<VoiceRuntimeContextValue>(
		() => ({
			manifest,
			enabled,
			capabilities,
			runtime: bundle?.runtime,
			audioSuspended,
			resumeAudio,
			downloadProgress,
			downloadError,
			dismissDownloadNotice,
			lastError,
			playingMessageId,
			playMessage,
			previewVoice,
			stopPlayback,
		}),
		[
			manifest,
			enabled,
			capabilities,
			bundle,
			audioSuspended,
			resumeAudio,
			downloadProgress,
			downloadError,
			dismissDownloadNotice,
			lastError,
			playingMessageId,
			playMessage,
			previewVoice,
			stopPlayback,
		],
	);

	return <VoiceRuntimeContext.Provider value={value}>{children}</VoiceRuntimeContext.Provider>;
}

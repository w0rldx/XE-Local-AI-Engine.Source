// App-root Web Speech runtime owner. Voice remains developer-mode + node-setting gated, but no longer allocates an
// AudioContext, starts a worker, opens a model cache, or performs any model/network request.

import { type ReactNode, useCallback, useEffect, useMemo, useRef, useState } from "react";

import { useDeveloperModeStore } from "@/core/dev-tools/stores/DeveloperModeStore";
import { detectVoiceCapabilities } from "@/core/runtime/CapabilityDetector";
import { sanitizeForSpeech } from "@/core/runtime/SentenceBuffer";
import { VoiceRuntime } from "@/core/runtime/VoiceRuntime";
import { detectAnswerLanguage } from "@/features/voice/DetectAnswerLanguage";
import { toSpeakableText } from "@/features/voice/SpeakableText";
import { useVoiceNodeSettings } from "@/features/voice/useVoiceNodeSettings";
import { useVoicePreferencesStore } from "@/features/voice/VoicePreferencesStore";
import { voicePreviewSample } from "@/features/voice/VoicePreviewSample";
import { VoiceRuntimeContext, type VoiceRuntimeContextValue } from "@/features/voice/VoiceRuntimeContext";
import { resolveOsVoiceLanguage } from "@/features/voice/WebSpeechVoiceCatalog";

export function ClientAiRuntimeProvider({ children }: { readonly children: ReactNode }) {
	const developerMode = useDeveloperModeStore((state) => state.developerMode);
	const nodeVoice = useVoiceNodeSettings(developerMode);
	const enabled = developerMode && nodeVoice.voiceFeatureEnabled;
	const defaultVoiceProfile = nodeVoice.defaultVoiceProfile;

	const runtimeRef = useRef<VoiceRuntime | undefined>(undefined);
	const [runtime, setRuntime] = useState<VoiceRuntime | undefined>();
	const [playingMessageId, setPlayingMessageId] = useState<string | undefined>();

	useEffect(() => {
		if (!enabled || runtimeRef.current) {
			return;
		}

		let cancelled = false;
		detectVoiceCapabilities()
			.then(({ webSpeech }) => {
				if (cancelled || !webSpeech.available) {
					return;
				}

				const created = new VoiceRuntime({ enabled: true });
				runtimeRef.current = created;
				setRuntime(created);
			})
			.catch(() => undefined);

		return () => {
			cancelled = true;
			runtimeRef.current?.dispose();
			runtimeRef.current = undefined;
			setRuntime(undefined);
		};
	}, [enabled]);

	const stopPlayback = useCallback(() => {
		runtimeRef.current?.stop();
		setPlayingMessageId(undefined);
	}, []);

	const playMessage = useCallback(
		async (messageId: string, text: string): Promise<void> => {
			const activeRuntime = runtimeRef.current;
			if (!activeRuntime) {
				return;
			}

			const sanitized = sanitizeForSpeech(toSpeakableText(text));
			if (sanitized.length === 0) {
				return;
			}

			const prefs = useVoicePreferencesStore.getState();
			const voiceId = prefs.voiceProfile || defaultVoiceProfile || undefined;
			const language = (voiceId ? resolveOsVoiceLanguage(voiceId) : undefined) ?? detectAnswerLanguage(sanitized);
			setPlayingMessageId(messageId);
			await activeRuntime.speak(sanitized, { language, voiceId, rate: prefs.speakingRate });
		},
		[defaultVoiceProfile],
	);

	const previewVoice = useCallback(async (voiceId: string): Promise<void> => {
		const activeRuntime = runtimeRef.current;
		if (!activeRuntime || !voiceId) {
			return;
		}

		// A stale neural-profile id from an older release (notably `af_heart`) resolves to no OS voice and therefore
		// safely previews with the browser's English/default voice instead of loading anything from the network.
		const language = resolveOsVoiceLanguage(voiceId) ?? "en";
		const prefs = useVoicePreferencesStore.getState();
		await activeRuntime.speak(voicePreviewSample(language), { language, voiceId, rate: prefs.speakingRate });
	}, []);

	const value = useMemo<VoiceRuntimeContextValue>(
		() => ({
			enabled,
			defaultVoiceProfile,
			runtime,
			playingMessageId,
			playMessage,
			previewVoice,
			stopPlayback,
		}),
		[enabled, defaultVoiceProfile, runtime, playingMessageId, playMessage, previewVoice, stopPlayback],
	);

	return <VoiceRuntimeContext.Provider value={value}>{children}</VoiceRuntimeContext.Provider>;
}

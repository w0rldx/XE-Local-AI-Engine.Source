// App-root Web Speech runtime owner. The operator's node setting is the only gate — voice is a thin SpeechSynthesis
// wrapper and never allocates an AudioContext, starts a worker, opens a model cache, or performs any model/network
// request, so there is nothing about it to keep behind developer mode.

import { type ReactNode, useCallback, useEffect, useMemo, useRef, useState } from "react";

import { VoiceRuntimeContext, type VoiceRuntimeContextValue } from "@/features/voice/hooks/VoiceRuntimeContext";
import { detectVoiceCapabilities } from "@/features/voice/models/CapabilityDetector";
import { detectAnswerLanguage } from "@/features/voice/models/DetectAnswerLanguage";
import { sanitizeForSpeech } from "@/features/voice/models/SentenceBuffer";
import { toSpeakableText } from "@/features/voice/models/SpeakableText";
import { voicePreviewSample } from "@/features/voice/models/VoicePreviewSample";
import { VoiceRuntime } from "@/features/voice/models/VoiceRuntime";
import { resolveOsVoiceLanguage } from "@/features/voice/models/WebSpeechVoiceCatalog";
import { useVoiceNodeSettings } from "@/features/voice/queries/useVoiceNodeSettings";
import { useVoicePreferencesStore } from "@/features/voice/stores/VoicePreferencesStore";

export function ClientAiRuntimeProvider({ children }: { readonly children: ReactNode }) {
	const nodeVoice = useVoiceNodeSettings();
	const enabled = nodeVoice.voiceFeatureEnabled;
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

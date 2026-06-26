// Short fixed sentence spoken when a user auditions a voice in Node Settings (the voice-preview affordance). The sample
// is keyed by the VOICE'S language, NOT the UI locale: a German voice must always preview German text so the
// German-routed provider (Web Speech) and a German voice are exercised faithfully, even when the UI is English. That
// language-coupling is why these strings live here as a constant map rather than in the i18n catalog (which follows the
// UI language). The on-screen button label IS user-facing and stays i18n-keyed in the component.

import type { VoiceLanguageCode } from "@/core/runtime/VoiceManifest";

const FALLBACK_PREVIEW_SAMPLE = "Hi! This is a preview of how this voice sounds.";

const VOICE_PREVIEW_SAMPLES: Record<string, string> = {
	en: FALLBACK_PREVIEW_SAMPLE,
	de: "Hallo! So klingt diese Stimme.",
};

/** Returns the preview sentence in the given voice's language, falling back to English for any uncovered language. */
export function voicePreviewSample(language: VoiceLanguageCode): string {
	const key = language.toLowerCase().slice(0, 2);
	return VOICE_PREVIEW_SAMPLES[key] ?? FALLBACK_PREVIEW_SAMPLE;
}

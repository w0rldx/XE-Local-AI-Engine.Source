import { create } from "zustand";

// Client-side voice preferences (UI-state), mirroring NodeChatPreferencesStore: zustand + guarded
// globalThis.localStorage, global (not per-conversation) keys, safe read/write with try/catch. These are the
// runtime knobs the operator-owned manifest does NOT cover — voice is OFF and autoplay is OFF by default so the
// dev-gated feature never speaks unless the user opts in. The selected profile + rate
// persist across reloads. No server mirroring (server-state lives in the manifest query).

const VOICE_ENABLED_STORAGE_KEY = "xe-voice-enabled";
const VOICE_PROFILE_STORAGE_KEY = "xe-voice-profile";
const VOICE_SPEAKING_RATE_STORAGE_KEY = "xe-voice-speaking-rate";
const VOICE_AUTOPLAY_STORAGE_KEY = "xe-voice-autoplay";

// Clamp range for the speaking rate (matches the Web Speech / Kokoro usable band). 1 = natural speed.
const MIN_RATE = 0.5;
const MAX_RATE = 2;
const DEFAULT_RATE = 1;

interface VoicePreferencesStore {
	voiceEnabled: boolean;
	voiceProfile: string;
	speakingRate: number;
	autoPlayAssistant: boolean;
	actions: {
		setVoiceEnabled: (value: boolean) => void;
		toggleVoiceEnabled: () => void;
		setVoiceProfile: (value: string) => void;
		setSpeakingRate: (value: number) => void;
		setAutoPlayAssistant: (value: boolean) => void;
	};
}

function readStoredString(key: string): string | undefined {
	try {
		return globalThis.localStorage?.getItem(key) ?? undefined;
	} catch {
		return undefined;
	}
}

function writeStoredValue(key: string, value: string): void {
	try {
		globalThis.localStorage?.setItem(key, value);
	} catch {
		// Ignore unavailable storage or quota errors; the in-memory preference still updates.
	}
}

function readStoredBoolean(key: string): boolean {
	return readStoredString(key) === "true";
}

function clampRate(value: number): number {
	if (!Number.isFinite(value)) {
		return DEFAULT_RATE;
	}

	return Math.min(MAX_RATE, Math.max(MIN_RATE, value));
}

function readStoredRate(): number {
	const stored = readStoredString(VOICE_SPEAKING_RATE_STORAGE_KEY);
	if (stored === undefined) {
		return DEFAULT_RATE;
	}

	const parsed = Number.parseFloat(stored);
	return Number.isNaN(parsed) ? DEFAULT_RATE : clampRate(parsed);
}

export const useVoicePreferencesStore = create<VoicePreferencesStore>()((set) => ({
	voiceEnabled: readStoredBoolean(VOICE_ENABLED_STORAGE_KEY),
	voiceProfile: readStoredString(VOICE_PROFILE_STORAGE_KEY) ?? "",
	speakingRate: readStoredRate(),
	autoPlayAssistant: readStoredBoolean(VOICE_AUTOPLAY_STORAGE_KEY),
	actions: {
		setVoiceEnabled: (value) => {
			writeStoredValue(VOICE_ENABLED_STORAGE_KEY, String(value));
			set({ voiceEnabled: value });
		},
		toggleVoiceEnabled: () => {
			set((state) => {
				const nextValue = !state.voiceEnabled;
				writeStoredValue(VOICE_ENABLED_STORAGE_KEY, String(nextValue));
				return { voiceEnabled: nextValue };
			});
		},
		setVoiceProfile: (value) => {
			writeStoredValue(VOICE_PROFILE_STORAGE_KEY, value);
			set({ voiceProfile: value });
		},
		setSpeakingRate: (value) => {
			const clamped = clampRate(value);
			writeStoredValue(VOICE_SPEAKING_RATE_STORAGE_KEY, String(clamped));
			set({ speakingRate: clamped });
		},
		setAutoPlayAssistant: (value) => {
			writeStoredValue(VOICE_AUTOPLAY_STORAGE_KEY, String(value));
			set({ autoPlayAssistant: value });
		},
	},
}));

export const voicePreferencesRateBounds = { min: MIN_RATE, max: MAX_RATE, default: DEFAULT_RATE } as const;

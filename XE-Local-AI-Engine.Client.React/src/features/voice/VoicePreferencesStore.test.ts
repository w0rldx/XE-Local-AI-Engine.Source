// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

async function loadStore(seed?: Record<string, string>) {
	localStorage.clear();
	if (seed) {
		for (const [key, value] of Object.entries(seed)) {
			localStorage.setItem(key, value);
		}
	}

	vi.resetModules();
	const module = await import("@/features/voice/VoicePreferencesStore");
	return module.useVoicePreferencesStore;
}

describe("VoicePreferencesStore", () => {
	beforeEach(() => localStorage.clear());
	afterEach(() => localStorage.clear());

	it("defaults to voice + autoplay off, empty profile, rate 1", async () => {
		const useStore = await loadStore();
		const state = useStore.getState();
		expect(state.voiceEnabled).toBe(false);
		expect(state.autoPlayAssistant).toBe(false);
		expect(state.voiceProfile).toBe("");
		expect(state.speakingRate).toBe(1);
	});

	it("hydrates persisted values on init", async () => {
		const useStore = await loadStore({
			"xe-voice-enabled": "true",
			"xe-voice-autoplay": "true",
			"xe-voice-profile": "af_heart",
			"xe-voice-speaking-rate": "1.5",
		});
		const state = useStore.getState();
		expect(state.voiceEnabled).toBe(true);
		expect(state.autoPlayAssistant).toBe(true);
		expect(state.voiceProfile).toBe("af_heart");
		expect(state.speakingRate).toBe(1.5);
	});

	it("round-trips a toggle through localStorage", async () => {
		const useStore = await loadStore();
		useStore.getState().actions.toggleVoiceEnabled();
		expect(useStore.getState().voiceEnabled).toBe(true);
		expect(localStorage.getItem("xe-voice-enabled")).toBe("true");
	});

	it("persists profile + autoplay writes", async () => {
		const useStore = await loadStore();
		useStore.getState().actions.setVoiceProfile("am_michael");
		useStore.getState().actions.setAutoPlayAssistant(true);
		expect(localStorage.getItem("xe-voice-profile")).toBe("am_michael");
		expect(localStorage.getItem("xe-voice-autoplay")).toBe("true");
	});

	it("clamps the speaking rate to the allowed band and persists the clamped value", async () => {
		const useStore = await loadStore();
		useStore.getState().actions.setSpeakingRate(9);
		expect(useStore.getState().speakingRate).toBe(2);
		expect(localStorage.getItem("xe-voice-speaking-rate")).toBe("2");

		useStore.getState().actions.setSpeakingRate(0.1);
		expect(useStore.getState().speakingRate).toBe(0.5);
	});

	it("falls back to rate 1 for a corrupt persisted value", async () => {
		const useStore = await loadStore({ "xe-voice-speaking-rate": "not-a-number" });
		expect(useStore.getState().speakingRate).toBe(1);
	});
});

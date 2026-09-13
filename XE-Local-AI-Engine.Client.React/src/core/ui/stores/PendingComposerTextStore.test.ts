// @vitest-environment jsdom

import { beforeEach, describe, expect, it } from "vitest";

import { usePendingComposerTextStore } from "@/core/ui/stores/PendingComposerTextStore";

describe("usePendingComposerTextStore", () => {
	beforeEach(() => {
		usePendingComposerTextStore.setState({ pendingText: "" });
		// The suite-wide i18n init (a `setupFiles` entry) makes i18next-browser-languagedetector cache the detected
		// language as `i18nextLng` in localStorage, so storage is not empty by the time this file runs. Clearing it
		// here keeps the strict `storage.length === 0` assertion below meaningful — it still fails if the store
		// itself ever starts persisting — rather than weakening it to ignore foreign keys. The detector's cache is
		// NOT disabled in src/i18n.ts: production seeds UserLanguageStore from that very key.
		globalThis.localStorage.clear();
	});

	it("hands the staged text back and empties itself", () => {
		usePendingComposerTextStore.getState().actions.setPendingText("a transcript");

		expect(usePendingComposerTextStore.getState().actions.consume()).toBe("a transcript");
		expect(usePendingComposerTextStore.getState().pendingText).toBe("");
	});

	// The composer consumes from a render effect, so a second consume must be a no-op rather than re-delivering.
	it("returns an empty string once consumed", () => {
		usePendingComposerTextStore.getState().actions.setPendingText("a transcript");
		usePendingComposerTextStore.getState().actions.consume();

		expect(usePendingComposerTextStore.getState().actions.consume()).toBe("");
	});

	// A transcript is whatever the microphone heard; persisting it would leave it in this browser's localStorage.
	it("never writes the staged text to browser storage", () => {
		usePendingComposerTextStore.getState().actions.setPendingText("a transcript");

		const storage = globalThis.localStorage;
		const stored = Object.keys(storage).map((key) => storage.getItem(key));
		expect(storage.length).toBe(0);
		expect(stored.some((value) => value?.includes("a transcript") === true)).toBe(false);
	});
});

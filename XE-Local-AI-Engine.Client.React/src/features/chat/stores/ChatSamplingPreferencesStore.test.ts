// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it } from "vitest";

const SAMPLING_OPTIONS_STORAGE_KEY = "xe-node-chat-sampling-options";

// The store reads localStorage once at module-init, so each test seeds storage then re-imports
// the module with a fresh registry to exercise the init path (mirrors NodeChatPreferencesStore.test).
async function loadStore(seed?: string) {
	localStorage.clear();
	if (seed !== undefined) {
		localStorage.setItem(SAMPLING_OPTIONS_STORAGE_KEY, seed);
	}

	const { vi } = await import("vitest");
	vi.resetModules();
	const module = await import("@/features/chat/stores/ChatSamplingPreferencesStore");
	return module.useChatSamplingPreferencesStore;
}

describe("ChatSamplingPreferencesStore", () => {
	beforeEach(() => {
		localStorage.clear();
	});

	afterEach(() => {
		localStorage.clear();
	});

	it("defaults to an empty options object when nothing is persisted", async () => {
		const useStore = await loadStore();
		expect(useStore.getState().options).toEqual({});
	});

	it("hydrates valid persisted options on init", async () => {
		const stored = JSON.stringify({ temperature: 0.7, topK: 40, seed: 42 });
		const useStore = await loadStore(stored);
		const state = useStore.getState();

		expect(state.options.temperature).toBe(0.7);
		expect(state.options.topK).toBe(40);
		expect(state.options.seed).toBe(42);
	});

	it("falls back to empty object when persisted JSON is malformed", async () => {
		const useStore = await loadStore("not-valid-json{{");
		expect(useStore.getState().options).toEqual({});
	});

	it("falls back to empty object when persisted value is an array (wrong shape)", async () => {
		const useStore = await loadStore(JSON.stringify([1, 2, 3]));
		expect(useStore.getState().options).toEqual({});
	});

	it("falls back to empty object when persisted value is null", async () => {
		const useStore = await loadStore("null");
		expect(useStore.getState().options).toEqual({});
	});

	it("setField updates a single field and persists it", async () => {
		const useStore = await loadStore();

		useStore.getState().actions.setField("temperature", 0.9);

		expect(useStore.getState().options.temperature).toBe(0.9);
		const stored = JSON.parse(localStorage.getItem(SAMPLING_OPTIONS_STORAGE_KEY) ?? "{}") as Record<string, unknown>;
		expect(stored["temperature"]).toBe(0.9);
	});

	it("setField clears a field when set to undefined", async () => {
		const useStore = await loadStore(JSON.stringify({ temperature: 0.5 }));

		useStore.getState().actions.setField("temperature", undefined);

		expect(useStore.getState().options.temperature).toBeUndefined();
	});

	it("reset clears all options and persists empty object", async () => {
		const useStore = await loadStore(JSON.stringify({ temperature: 0.5, topK: 20 }));

		useStore.getState().actions.reset();

		expect(useStore.getState().options).toEqual({});
		expect(localStorage.getItem(SAMPLING_OPTIONS_STORAGE_KEY)).toBe("{}");
	});

	it("setField preserves other fields when updating one", async () => {
		const useStore = await loadStore(JSON.stringify({ temperature: 0.5, topP: 0.9 }));

		useStore.getState().actions.setField("topK", 30);

		const { options } = useStore.getState();
		expect(options.temperature).toBe(0.5);
		expect(options.topP).toBe(0.9);
		expect(options.topK).toBe(30);
	});

	// Bug-reproduction: string values written to localStorage by older builds (partial NumberInput
	// input before the dialog-layer coercion fix) must be coerced to real numbers on hydrate so
	// they never reach the wire as a JSON string (System.Text.Json rejects string for float?).
	it("coerces a string-typed numeric field to a number on hydrate (repro: minP stored as '05')", async () => {
		// Number("05") === 5, which is finite — store keeps it as the number 5.
		const useStore = await loadStore(JSON.stringify({ temperature: 0.5, minP: "05" }));
		const { options } = useStore.getState();

		expect(options.temperature).toBe(0.5);
		// Coerced to the number 5 — no longer a string
		expect(options.minP).toBe(5);
		expect(typeof options.minP).toBe("number");
	});

	it("drops non-numeric string fields on hydrate", async () => {
		const useStore = await loadStore(JSON.stringify({ topP: "abc" }));
		expect(useStore.getState().options.topP).toBeUndefined();
	});

	it("accepts stop[] when it is an array of strings", async () => {
		const useStore = await loadStore(JSON.stringify({ stop: ["</s>", "###"] }));
		expect(useStore.getState().options.stop).toEqual(["</s>", "###"]);
	});

	it("drops stop when it is not an array", async () => {
		const useStore = await loadStore(JSON.stringify({ stop: "not-an-array" }));
		expect(useStore.getState().options.stop).toBeUndefined();
	});
});

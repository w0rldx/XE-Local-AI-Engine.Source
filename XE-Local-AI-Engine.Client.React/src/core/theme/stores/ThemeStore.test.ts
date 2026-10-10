// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

const THEME_STORAGE_KEY = "theme-storage";
const MANTINE_COLOR_SCHEME_KEY = "mantine-color-scheme";

// Zustand's persist hydrates synchronously from localStorage when the module creates the store, so each test
// seeds storage and re-imports the module with a fresh registry to exercise the hydration (and migration) path.
async function loadStore(persistedState?: Record<string, unknown>) {
	if (persistedState !== undefined) {
		localStorage.setItem(THEME_STORAGE_KEY, JSON.stringify({ state: persistedState, version: 0 }));
	}

	vi.resetModules();
	const module = await import("@/core/theme/stores/ThemeStore");
	return module.useThemeStore;
}

describe("ThemeStore", () => {
	beforeEach(() => {
		localStorage.clear();
	});

	afterEach(() => {
		localStorage.clear();
	});

	it("defaults the accent colour to null when nothing is persisted", async () => {
		const useStore = await loadStore();

		expect(useStore.getState().accentColor).toBeNull();
	});

	it("hydrates a persisted #rrggbb accent, lower-cased", async () => {
		const useStore = await loadStore({ accentColor: "#1C7ED6" });

		expect(useStore.getState().accentColor).toBe("#1c7ed6");
	});

	it.each([["#fff"], ["blue"], ["#1c7ed6ff"], [42], [null]])(
		"drops an invalid persisted accent %j to null",
		async (accentColor) => {
			const useStore = await loadStore({ accentColor });

			expect(useStore.getState().accentColor).toBeNull();
		},
	);

	// The retired configurator persisted a whole palette here; it must not leak back in as an accent.
	it("ignores a persisted themeConfiguration from the retired configurator", async () => {
		const useStore = await loadStore({ themeConfiguration: { palette: { primary: { main: "#123456" } } } });

		expect(useStore.getState().accentColor).toBeNull();
	});

	it("setAccentColor stores a valid hex and persists it, and null resets it", async () => {
		const useStore = await loadStore();

		useStore.getState().setAccentColor("#12B886");
		expect(useStore.getState().accentColor).toBe("#12b886");
		expect(localStorage.getItem(THEME_STORAGE_KEY)).toContain('"accentColor":"#12b886"');

		useStore.getState().setAccentColor(null);
		expect(useStore.getState().accentColor).toBeNull();
	});

	it("migrates a persisted dark mode into Mantine's colour-scheme key when that key is unset", async () => {
		await loadStore({ mode: "dark" });

		expect(localStorage.getItem(MANTINE_COLOR_SCHEME_KEY)).toBe("dark");
	});

	it("never overwrites a colour scheme the user already chose through Mantine", async () => {
		localStorage.setItem(MANTINE_COLOR_SCHEME_KEY, "auto");

		await loadStore({ mode: "dark" });

		expect(localStorage.getItem(MANTINE_COLOR_SCHEME_KEY)).toBe("auto");
	});

	it("writes no colour-scheme key when the persisted record carries no mode", async () => {
		await loadStore({ accentColor: "#1c7ed6" });

		expect(localStorage.getItem(MANTINE_COLOR_SCHEME_KEY)).toBeNull();
	});
});

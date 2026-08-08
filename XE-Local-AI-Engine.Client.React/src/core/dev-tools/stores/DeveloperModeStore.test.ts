// @vitest-environment jsdom

import { afterEach, beforeEach, describe, expect, it } from "vitest";

const DEVELOPER_MODE_STORAGE_KEY = "xe-developer-mode";

async function loadStore(seed?: string) {
	localStorage.clear();
	if (seed !== undefined) {
		localStorage.setItem(DEVELOPER_MODE_STORAGE_KEY, seed);
	}

	const { vi } = await import("vitest");
	vi.resetModules();
	const module = await import("@/core/dev-tools/stores/DeveloperModeStore");
	return module.useDeveloperModeStore;
}

describe("DeveloperModeStore", () => {
	beforeEach(() => {
		localStorage.clear();
	});

	afterEach(() => {
		localStorage.clear();
	});

	it("defaults to false when nothing is persisted", async () => {
		const useStore = await loadStore();
		expect(useStore.getState().developerMode).toBe(false);
	});

	it("hydrates true from localStorage on init", async () => {
		const useStore = await loadStore("true");
		expect(useStore.getState().developerMode).toBe(true);
	});

	it("hydrates false from localStorage on init", async () => {
		const useStore = await loadStore("false");
		expect(useStore.getState().developerMode).toBe(false);
	});

	it("setDeveloperMode sets true and persists", async () => {
		const useStore = await loadStore();

		useStore.getState().actions.setDeveloperMode(true);

		expect(useStore.getState().developerMode).toBe(true);
		expect(localStorage.getItem(DEVELOPER_MODE_STORAGE_KEY)).toBe("true");
	});

	it("setDeveloperMode sets false and persists", async () => {
		const useStore = await loadStore("true");

		useStore.getState().actions.setDeveloperMode(false);

		expect(useStore.getState().developerMode).toBe(false);
		expect(localStorage.getItem(DEVELOPER_MODE_STORAGE_KEY)).toBe("false");
	});

	it("toggle flips false to true and persists", async () => {
		const useStore = await loadStore();

		useStore.getState().actions.toggle();

		expect(useStore.getState().developerMode).toBe(true);
		expect(localStorage.getItem(DEVELOPER_MODE_STORAGE_KEY)).toBe("true");
	});

	it("toggle flips true to false and persists", async () => {
		const useStore = await loadStore("true");

		useStore.getState().actions.toggle();

		expect(useStore.getState().developerMode).toBe(false);
		expect(localStorage.getItem(DEVELOPER_MODE_STORAGE_KEY)).toBe("false");
	});

	it("double toggle returns to original state", async () => {
		const useStore = await loadStore();

		useStore.getState().actions.toggle();
		useStore.getState().actions.toggle();

		expect(useStore.getState().developerMode).toBe(false);
	});
});

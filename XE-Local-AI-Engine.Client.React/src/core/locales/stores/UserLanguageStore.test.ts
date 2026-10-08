// @vitest-environment jsdom

import { afterEach, describe, expect, it, vi } from "vitest";

describe("UserLanguageStore", () => {
	afterEach(() => {
		vi.restoreAllMocks();
	});

	// The store reads storage while its module evaluates, so a throw there broke every module that imports it.
	it("falls back to English when the browser blocks site data", async () => {
		vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
			throw new DOMException("blocked", "SecurityError");
		});
		vi.resetModules();

		const { useUserLanguageStore } = await import("@/core/locales/stores/UserLanguageStore");

		expect(useUserLanguageStore.getState().selectedApplicationLanguage).toBe("en");
	});
});

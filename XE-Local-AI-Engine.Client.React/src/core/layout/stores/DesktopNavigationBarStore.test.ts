// @vitest-environment jsdom

import { afterEach, describe, expect, it, vi } from "vitest";

describe("DesktopNavigationBarStore", () => {
	afterEach(() => {
		vi.restoreAllMocks();
	});

	// The store reads storage while the Layout chunk evaluates, so a throw there failed every authenticated route.
	it("falls back to defaults when the browser blocks site data", async () => {
		vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
			throw new DOMException("blocked", "SecurityError");
		});
		vi.resetModules();

		const { useDesktopNavigationBarStore } = await import("@/core/layout/stores/DesktopNavigationBarStore");

		expect(useDesktopNavigationBarStore.getState().sidebarState).toBe(false);
		expect(useDesktopNavigationBarStore.getState().openGroups).toEqual({});
	});
});

// @vitest-environment jsdom

import { readFileSync } from "node:fs";
import { dirname, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import { afterEach, describe, expect, it, vi } from "vitest";

import { colorSchemeStorageKey } from "@/core/theme/stores/ThemeStore";

// The shipped classic script itself runs here, not a copy of its logic.
const root = resolve(dirname(fileURLToPath(import.meta.url)), "..", "..", "..", "..");
const indexHtml = readFileSync(resolve(root, "index.html"), "utf8");
const prePaintScript = readFileSync(resolve(root, "public", "ColorSchemeBeforePaint.js"), "utf8");

function runPrePaintScript(systemPrefersDark: boolean): string | null {
	vi.stubGlobal(
		"matchMedia",
		vi.fn((query: string) => ({ matches: systemPrefersDark && query === "(prefers-color-scheme: dark)" })),
	);
	document.documentElement.removeAttribute("data-mantine-color-scheme");
	new Function(prePaintScript)();
	return document.documentElement.getAttribute("data-mantine-color-scheme");
}

describe("index.html pre-paint colour scheme", () => {
	afterEach(() => {
		localStorage.clear();
		vi.unstubAllGlobals();
		vi.restoreAllMocks();
	});

	it("loads the script from index.html beside a color-scheme meta, on ThemeProvider's storage key", () => {
		expect(indexHtml).toContain('<meta name="color-scheme" content="light dark" />');
		expect(indexHtml).toContain('<script src="/ColorSchemeBeforePaint.js"></script>');
		expect(prePaintScript).toContain(`"${colorSchemeStorageKey}"`);
	});

	it.each([
		["an explicit dark choice on a light system", "dark", false, "dark"],
		["an explicit light choice on a dark system", "light", true, "light"],
		["auto on a dark system", "auto", true, "dark"],
		["auto on a light system", "auto", false, "light"],
		["no stored choice on a dark system", null, true, "dark"],
		["an unknown stored value on a light system", "sepia", false, "light"],
	])("paints %s", (_name, stored, systemPrefersDark, expected) => {
		if (stored !== null) {
			localStorage.setItem(colorSchemeStorageKey, stored);
		}

		expect(runPrePaintScript(systemPrefersDark)).toBe(expected);
	});

	// Storage throws in some private modes; the page must still load, and Mantine then picks the scheme on mount.
	it("leaves the scheme to Mantine when storage throws", () => {
		vi.spyOn(Storage.prototype, "getItem").mockImplementation(() => {
			throw new Error("blocked");
		});

		expect(runPrePaintScript(true)).toBeNull();
	});
});

import { describe, expect, it } from "vitest";

import de from "@/locales/de.json";
import en from "@/locales/en.json";

// The Diagnostics panel surfaces only keyed strings. This guards that every
// `diagnostics.*` key exists in BOTH locales with an identical key set (the global I18n.test.ts asserts
// whole-file parity; this is the focused diagnostics-section regression).
function collectKeyPaths(node: unknown, prefix = ""): string[] {
	if (node === null || typeof node !== "object" || Array.isArray(node)) {
		return [prefix];
	}
	return Object.entries(node as Record<string, unknown>).flatMap(([key, value]) =>
		collectKeyPaths(value, prefix ? `${prefix}.${key}` : key),
	);
}

describe("diagnostics i18n parity", () => {
	it("has an identical set of diagnostics keys in en and de", () => {
		const enKeys = collectKeyPaths(en.diagnostics).sort();
		const deKeys = collectKeyPaths(de.diagnostics).sort();

		expect(enKeys.length).toBeGreaterThan(0);
		expect(enKeys).toEqual(deKeys);
	});

	it("keys the diagnostics navigation label in both locales", () => {
		expect(typeof en.navigation.diagnostics).toBe("string");
		expect(typeof de.navigation.diagnostics).toBe("string");
	});
});

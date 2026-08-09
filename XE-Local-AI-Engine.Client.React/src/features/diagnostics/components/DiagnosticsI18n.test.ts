import { describe, expect, it } from "vitest";

import en from "@/locales/en.json";
import { nonEnglishLocales } from "@/test/Locales";

// The Diagnostics panel surfaces only keyed strings. This guards that every
// `diagnostics.*` key exists in every locale with an identical key set (the global I18n.test.ts asserts
// whole-file parity; this is the focused diagnostics-section regression).
function collectKeyPaths(node: unknown, prefix = ""): string[] {
	if (node === null || typeof node !== "object" || Array.isArray(node)) {
		return [prefix];
	}
	return Object.entries(node as Record<string, unknown>).flatMap(([key, value]) =>
		collectKeyPaths(value, prefix ? `${prefix}.${key}` : key),
	);
}

const enDiagnosticsKeys = collectKeyPaths(en.diagnostics).sort();

describe("diagnostics i18n parity", () => {
	it("has diagnostics keys in en", () => {
		expect(enDiagnosticsKeys.length).toBeGreaterThan(0);
	});

	it.each(nonEnglishLocales)("has an identical set of diagnostics keys in en and $code", ({ resource }) => {
		const diagnostics = (resource as Record<string, unknown>)["diagnostics"];
		expect(collectKeyPaths(diagnostics).sort()).toEqual(enDiagnosticsKeys);
	});

	it("keys the diagnostics navigation label in en", () => {
		expect(typeof en.navigation.diagnostics).toBe("string");
	});

	it.each(nonEnglishLocales)("keys the diagnostics navigation label in $code", ({ resource }) => {
		const navigation = (resource as Record<string, unknown>)["navigation"] as Record<string, unknown>;
		expect(typeof navigation["diagnostics"]).toBe("string");
	});
});

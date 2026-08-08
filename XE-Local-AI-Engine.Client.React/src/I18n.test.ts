import { createInstance } from "i18next";
import { describe, expect, it } from "vitest";

import de from "@/locales/de.json";
import en from "@/locales/en.json";

// Regression for the toast HTML-entity bug: i18next core defaults `interpolation.escapeValue` to true,
// which HTML-escapes interpolated values ("/" → "&#x2F;") and made model names like "hf.co/unsloth/…"
// print the literal entity in Mantine toasts. The app sets `escapeValue: false` in src/i18n.ts (React
// escapes text nodes at render → still XSS-safe). This test pins that config so a regression is caught.
async function interpolate(value: string, options?: { escapeValue: boolean }): Promise<string> {
	const instance = createInstance({
		lng: "en",
		resources: { en: { translation: { greeting: "Pulling {{model}}" } } },
		interpolation: options,
	});
	await instance.init();
	return instance.t("greeting", { model: value });
}

describe("i18n interpolation escaping", () => {
	it("does not HTML-escape interpolated values when escapeValue is false", async () => {
		const result = await interpolate("hf.co/unsloth/model", { escapeValue: false });

		expect(result).toBe("Pulling hf.co/unsloth/model");
		expect(result).not.toContain("&#x2F;");
		expect(result).not.toContain("&amp;");
	});

	it("would escape the slash with the i18next default (documents the bug we disabled)", async () => {
		const result = await interpolate("hf.co/unsloth/model");

		// Confirms the default behavior we are overriding: the bug reproduces only when escaping is on.
		expect(result).toContain("&#x2F;");
	});
});

// Collects every leaf-key dotted path of a nested translation resource. Leaves are non-object values (string / number);
// arrays are treated as leaves. Used to assert the en and de locales are structurally identical (same keys, not just
// the same count), which guards against an orphaned key surviving in only one locale.
function collectKeyPaths(node: unknown, prefix = ""): string[] {
	if (node === null || typeof node !== "object" || Array.isArray(node)) {
		return [prefix];
	}
	const paths: string[] = [];
	for (const [key, value] of Object.entries(node as Record<string, unknown>)) {
		paths.push(...collectKeyPaths(value, prefix ? `${prefix}.${key}` : key));
	}
	return paths;
}

describe("i18n locale parity", () => {
	it("has an identical set of translation keys in en and de", () => {
		const enKeys = collectKeyPaths(en).sort();
		const deKeys = collectKeyPaths(de).sort();

		expect(enKeys.length).toBe(deKeys.length);
		expect(enKeys).toEqual(deKeys);
	});

	it("has no orphaned approved-image keys in either locale", () => {
		const enKeys = collectKeyPaths(en);
		const deKeys = collectKeyPaths(de);

		expect(enKeys.some((key) => key.includes("approvedImages"))).toBe(false);
		expect(deKeys.some((key) => key.includes("approvedImages"))).toBe(false);
	});
});

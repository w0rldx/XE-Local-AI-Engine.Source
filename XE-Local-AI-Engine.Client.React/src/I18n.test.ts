import { createInstance } from "i18next";
import { describe, expect, it } from "vitest";

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

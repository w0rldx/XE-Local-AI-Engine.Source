// Verifies that all appUpdate i18n keys added to en.json exist in de.json with matching paths.
// This test is intentionally not jsdom-scoped — it operates purely on the JSON locale files.

import { describe, expect, it } from "vitest";

import en from "@/locales/en.json";
import de from "@/locales/de.json";

type LocaleShape = Record<string, unknown>;

// Recursively collect all leaf key paths from an object.
function collectKeys(obj: LocaleShape, prefix = ""): string[] {
	const result: string[] = [];
	for (const [k, v] of Object.entries(obj)) {
		const path = prefix ? `${prefix}.${k}` : k;
		if (v !== null && typeof v === "object" && !Array.isArray(v)) {
			result.push(...collectKeys(v as LocaleShape, path));
		} else {
			result.push(path);
		}
	}
	return result;
}

// Resolve a dot-path against an object, returning undefined if any segment is missing.
function resolvePath(obj: LocaleShape, path: string): unknown {
	return path.split(".").reduce<unknown>((acc, segment) => {
		if (acc === undefined || acc === null || typeof acc !== "object") { return undefined; }
		return (acc as LocaleShape)[segment];
	}, obj);
}

const enPages = (en as LocaleShape)["pages"] as LocaleShape;
const dePages = (de as LocaleShape)["pages"] as LocaleShape;

describe("app-update i18n key parity (en ↔ de)", () => {
	const appUpdateEnKeys = collectKeys(enPages)
		.filter((k) => k.startsWith("about.appUpdate."))
		.map((k) => `pages.${k}`);

	it("has at least one appUpdate key in en.json", () => {
		expect(appUpdateEnKeys.length).toBeGreaterThan(0);
	});

	it("every en.json appUpdate key exists in de.json", () => {
		const missing: string[] = [];
		for (const key of appUpdateEnKeys) {
			const value = resolvePath(de as LocaleShape, key);
			if (value === undefined) {
				missing.push(key);
			}
		}
		expect(missing, `Keys present in en.json but missing in de.json: ${missing.join(", ")}`).toHaveLength(0);
	});

	it("every de.json appUpdate key exists in en.json", () => {
		const deAppUpdateKeys = collectKeys(dePages)
			.filter((k) => k.startsWith("about.appUpdate."))
			.map((k) => `pages.${k}`);

		const missing: string[] = [];
		for (const key of deAppUpdateKeys) {
			const value = resolvePath(en as LocaleShape, key);
			if (value === undefined) {
				missing.push(key);
			}
		}
		expect(missing, `Keys present in de.json but missing in en.json: ${missing.join(", ")}`).toHaveLength(0);
	});

	it("en and de appUpdate key counts are equal", () => {
		const deAppUpdateKeys = collectKeys(dePages).filter((k) =>
			k.startsWith("about.appUpdate."),
		);
		expect(appUpdateEnKeys.length).toBe(deAppUpdateKeys.length);
	});
});

const enVoice = (en as LocaleShape)["voice"] as LocaleShape;
const deVoice = (de as LocaleShape)["voice"] as LocaleShape;

describe("voice i18n key parity (en ↔ de)", () => {
	const voiceEnKeys = collectKeys(enVoice).map((k) => `voice.${k}`);
	const voiceDeKeys = collectKeys(deVoice).map((k) => `voice.${k}`);

	it("has at least one voice key in en.json", () => {
		expect(voiceEnKeys.length).toBeGreaterThan(0);
	});

	it("every en.json voice key exists in de.json", () => {
		const missing = voiceEnKeys.filter((key) => resolvePath(de as LocaleShape, key) === undefined);
		expect(missing, `Keys present in en.json but missing in de.json: ${missing.join(", ")}`).toHaveLength(0);
	});

	it("every de.json voice key exists in en.json", () => {
		const missing = voiceDeKeys.filter((key) => resolvePath(en as LocaleShape, key) === undefined);
		expect(missing, `Keys present in de.json but missing in en.json: ${missing.join(", ")}`).toHaveLength(0);
	});

	it("en and de voice key counts are equal", () => {
		expect(voiceEnKeys.length).toBe(voiceDeKeys.length);
	});
});

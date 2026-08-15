// Verifies that the training i18n keys stay in parity between en.json and every other locale. Parity is opt-in per
// feature, so the training area owns this file the way app-update owns its own. Like that one, it is not jsdom-scoped:
// it operates purely on the JSON locale files.

import { describe, expect, it } from "vitest";

import en from "@/locales/en.json";
import { nonEnglishLocales } from "@/test/Locales";

type LocaleShape = Record<string, unknown>;

function collectKeys(obj: LocaleShape, prefix = ""): string[] {
	const result: string[] = [];
	for (const [key, value] of Object.entries(obj)) {
		const path = prefix ? `${prefix}.${key}` : key;
		if (value !== null && typeof value === "object" && !Array.isArray(value)) {
			result.push(...collectKeys(value as LocaleShape, path));
		} else {
			result.push(path);
		}
	}
	return result;
}

function resolvePath(obj: LocaleShape, path: string): unknown {
	return path.split(".").reduce<unknown>((acc, segment) => {
		if (acc === undefined || acc === null || typeof acc !== "object") {
			return undefined;
		}
		return (acc as LocaleShape)[segment];
	}, obj);
}

function sectionKeys(resource: LocaleShape, rootSection: string, subPrefix = ""): string[] {
	const root = resource[rootSection] as LocaleShape | undefined;
	if (!root) {
		return [];
	}
	return collectKeys(root)
		.filter((key) => key.startsWith(subPrefix))
		.map((key) => `${rootSection}.${key}`);
}

// The training area spans three roots: its own top-level namespace, its page titles under "pages", and its two
// navigation labels.
const sections = [
	{ name: "training", enKeys: sectionKeys(en as LocaleShape, "training") },
	{ name: "trainingPages", enKeys: sectionKeys(en as LocaleShape, "pages", "training.") },
	{ name: "trainingNavigation", enKeys: sectionKeys(en as LocaleShape, "navigation", "training") },
] as const;

describe.each(sections)("$name i18n key parity (en ↔ every locale)", ({ name, enKeys }) => {
	it(`has at least one ${name} key in en.json`, () => {
		expect(enKeys.length).toBeGreaterThan(0);
	});

	it.each(nonEnglishLocales)(`every en.json ${name} key exists in $code`, ({ resource }) => {
		const missing = enKeys.filter((key) => resolvePath(resource as LocaleShape, key) === undefined);
		expect(missing, `Missing in locale: ${missing.join(", ")}`).toHaveLength(0);
	});
});

// Verifies that the work-session i18n keys stay in parity between en.json and every other locale. Parity is opt-in
// per feature, so this area owns its own file the way training does. Not jsdom-scoped: it operates purely on the
// JSON locale files.

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

const sections = [
	{ name: "workSessionsPages", enKeys: sectionKeys(en as LocaleShape, "pages", "workSessions.") },
	{ name: "workSessionsNavigation", enKeys: sectionKeys(en as LocaleShape, "navigation", "workSessions") },
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

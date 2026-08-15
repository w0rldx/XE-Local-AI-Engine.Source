// Verifies that appUpdate and voice i18n keys stay in parity between en.json and every other locale.
// This test is intentionally not jsdom-scoped — it operates purely on the JSON locale files.

import { describe, expect, it } from "vitest";

import en from "@/locales/en.json";
import { nonEnglishLocales } from "@/test/Locales";

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

// Collects the fully-qualified key paths of one translation section (e.g. "voice", or the
// "about.appUpdate." subtree under "pages"). Returns them prefixed so they resolve against the
// locale root.
function sectionKeys(resource: LocaleShape, rootSection: string, subPrefix = ""): string[] {
	const root = resource[rootSection] as LocaleShape | undefined;
	if (!root) {
		return [];
	}
	return collectKeys(root)
		.filter((k) => k.startsWith(subPrefix))
		.map((k) => `${rootSection}.${k}`);
}

// Each section that had a dedicated parity guard, now checked against every non-English locale.
const sections = [
	{ name: "appUpdate", enKeys: sectionKeys(en as LocaleShape, "pages", "about.appUpdate.") },
	{ name: "voice", enKeys: sectionKeys(en as LocaleShape, "voice") },
	{ name: "training", enKeys: sectionKeys(en as LocaleShape, "pages", "training.") },
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

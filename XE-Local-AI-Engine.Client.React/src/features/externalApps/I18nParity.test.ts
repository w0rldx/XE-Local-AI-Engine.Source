// Verifies that the External Apps i18n keys stay in parity between en.json and every other locale, and that every
// closed vocabulary this feature looks up by value has a label. Parity is opt-in per feature, so this area owns its
// own file the way Development Workflows does. Not jsdom-scoped: it operates purely on the JSON locale files.
//
// The label maps are the half that fails SILENTLY. A status, failure category, runtime status, event kind, conflict
// type, permission name or blocked reason is looked up by its narrowed value, so a member without a label renders its
// own key at an operator — no compile error, no failing render, just `pages.externalApps.status.Resetting` on screen.

import { describe, expect, it } from "vitest";

import {
	containerRuntimeStatuses,
	externalAppEventKinds,
	externalAppFailureCategories,
	externalAppPermissionNames,
	externalAppStatuses,
} from "@/features/externalApps/models/ExternalAppModels";
import { externalAppConflictTypes } from "@/features/externalApps/api/ExternalAppConflict";
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
	{ name: "externalAppsPages", enKeys: sectionKeys(en as LocaleShape, "pages", "externalApps.") },
	{ name: "externalAppsNavigation", enKeys: sectionKeys(en as LocaleShape, "navigation", "externalApps") },
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

describe("External Apps enum label maps are complete in en.json", () => {
	const vocabularies = [
		{ section: "status", members: externalAppStatuses },
		{ section: "failure", members: externalAppFailureCategories },
		{ section: "runtime.status", members: containerRuntimeStatuses },
		// The hints say what to DO about a status, so the two maps carry the same seven members.
		{ section: "runtime.hint", members: containerRuntimeStatuses },
		// The sixteen kinds plus the fallback a newer server's kind falls into.
		{ section: "events.kind", members: [...externalAppEventKinds, "unknown"] },
		{ section: "permissions.added", members: externalAppPermissionNames },
		// Keyed by the map's own member names, which is how `externalAppConflictMessageKey` builds the key.
		{ section: "conflict", members: Object.keys(externalAppConflictTypes) },
		// The seven blocked reasons, shared by install and update — `CatalogMissing` is reachable only from update.
		{
			section: "install.blocked",
			members: [
				"GpuNotSupported",
				"RuntimeIncompatible",
				"RuntimeUnavailable",
				"InsufficientMemory",
				"InsufficientDisk",
				"AlreadyInstalled",
				"CatalogMissing",
			],
		},
	] as const;

	it.each(vocabularies)("$section has a label for every member", ({ section, members }) => {
		for (const member of members) {
			expect(resolvePath(en as LocaleShape, `pages.externalApps.${section}.${member}`), `${section}.${member}`).toBeTypeOf(
				"string",
			);
		}
	});

	it("labels all three host-file levels and all three GPU levels", () => {
		for (const level of ["none", "read", "readWrite"]) {
			expect(resolvePath(en as LocaleShape, `pages.externalApps.permissions.hostFiles.${level}`), level).toBeTypeOf("string");
		}
		for (const level of ["none", "optional", "required"]) {
			expect(resolvePath(en as LocaleShape, `pages.externalApps.permissions.gpu.${level}`), level).toBeTypeOf("string");
		}
	});

	it("gives update the same seven blocked reasons install has, by key", () => {
		const install = Object.keys(resolvePath(en as LocaleShape, "pages.externalApps.install.blocked") as LocaleShape);
		const update = Object.keys(resolvePath(en as LocaleShape, "pages.externalApps.update.blocked") as LocaleShape);
		expect(update.toSorted()).toEqual(install.toSorted());
		expect(install).toHaveLength(7);
	});

	it("never claims the network is denied, because V1 enforces no outbound restriction", () => {
		const permissions = resolvePath(en as LocaleShape, "pages.externalApps.permissions") as LocaleShape;
		for (const key of ["internet", "localNetwork", "networkTitle"]) {
			expect(String(permissions[key]).toLowerCase()).not.toContain("denied");
		}
		// The two deleted keys: a "no internet" line would be a claim the runtime does not enforce.
		expect(permissions["noInternet"]).toBeUndefined();
		expect(permissions["noLocalNetwork"]).toBeUndefined();
	});
});

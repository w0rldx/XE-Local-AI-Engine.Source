import { checkI18nDefaults, findI18nDefaultDrift, findTranslationDefaults, flattenBundle } from "./CheckI18nDefaults.mjs";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

const frontendRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");

const bundle = {
	"pages.chat.send": "Send",
	"pages.chat.title": "Chat window",
	"pages.chat.items_one": "{{count}} item",
	"pages.chat.items_other": "{{count}} items",
};

test("a default that matches the bundle is not drift", () => {
	const { drifted, missing } = findI18nDefaultDrift(`const label = t("pages.chat.send", "Send");`, bundle);
	assert.deepEqual(drifted, []);
	assert.deepEqual(missing, []);
});

test("a differing default is reported with its key, line, and both strings", () => {
	const { drifted } = findI18nDefaultDrift(`const a = 1;\nconst title = t("pages.chat.title", "Local chat");`, bundle);
	assert.deepEqual(drifted, [{ line: 2, key: "pages.chat.title", codeDefault: "Local chat", bundleValue: "Chat window" }]);
});

test("the { defaultValue } call shape is detected too", () => {
	const source = `const title = t("pages.chat.title", { defaultValue: "Local chat", ns: "app" });`;
	assert.deepEqual(findTranslationDefaults(source), [{ line: 1, key: "pages.chat.title", codeDefault: "Local chat" }]);
	assert.equal(findI18nDefaultDrift(source, bundle).drifted.length, 1);
});

test("a plural key counts as present, and either form may be the in-code default", () => {
	const other = findI18nDefaultDrift(`t("pages.chat.items", "{{count}} items", { count });`, bundle);
	assert.deepEqual(other.missing, []);
	assert.deepEqual(other.drifted, []);
	assert.deepEqual(findI18nDefaultDrift(`t("pages.chat.items", "{{count}} item", { count });`, bundle).drifted, []);
	// A literal matching NEITHER plural form is still drift.
	assert.equal(findI18nDefaultDrift(`t("pages.chat.items", "{{count}} item(s)", { count });`, bundle).drifted.length, 1);
});

// A call with no default renders nothing but the raw dotted key when the bundle has no entry, which is the worst of
// the three failures — so presence is checked for every literal key, default or not.
test("a literal key with no default is a call site too, and is missing when the bundle lacks it", () => {
	assert.deepEqual(findTranslationDefaults(`const label = t("pages.chat.send");`), [
		{ line: 1, key: "pages.chat.send", codeDefault: undefined },
	]);

	const present = findI18nDefaultDrift(`t("pages.chat.send");`, bundle);
	assert.deepEqual(present.missing, []);
	assert.deepEqual(present.drifted, []);

	const absent = findI18nDefaultDrift(`t("pages.chat.absent");`, bundle);
	assert.deepEqual(absent.drifted, []);
	assert.deepEqual(absent.missing, [{ line: 1, key: "pages.chat.absent", codeDefault: undefined }]);

	// An options object that carries no defaultValue is the same case, and a plural family still counts as present.
	assert.deepEqual(findI18nDefaultDrift(`t("pages.chat.items", { count });`, bundle).missing, []);
});

// `flatten` used to build a plain object, so every Object.prototype member answered `key in bundle` with true and a
// key named after one looked present while holding a function.
test("a key named after an Object.prototype member is not mistaken for a bundle entry", () => {
	const flattened = flattenBundle({ pages: { chat: { send: "Send" } } });
	assert.equal(Object.getPrototypeOf(flattened), null);
	assert.deepEqual(findI18nDefaultDrift(`t("constructor", "Nope");`, flattened).missing, [
		{ line: 1, key: "constructor", codeDefault: "Nope" },
	]);
	assert.deepEqual(findI18nDefaultDrift(`t("toString");`, flattened).missing, [
		{ line: 1, key: "toString", codeDefault: undefined },
	]);
});

test("a t() call on an object (i18n.t, props.t) is scanned, and a dynamic key is skipped", () => {
	assert.equal(findTranslationDefaults('i18n.t("pages.chat.send", "Send");').length, 1);
	assert.deepEqual(findTranslationDefaults(`t(\`pages.\${area}.send\`, "Send");`), []);
});

function fixtureRoot({ source, en }) {
	const root = mkdtempSync(join(tmpdir(), "check-i18n-defaults-"));
	mkdirSync(join(root, "src", "locales"), { recursive: true });
	writeFileSync(join(root, "src", "Widget.tsx"), source, "utf8");
	writeFileSync(join(root, "src", "locales", "en.json"), JSON.stringify(en), "utf8");
	return root;
}

test("a key missing from en.json fails by default, and XE_I18N_MISSING_KEYS=warn downgrades it", () => {
	const root = fixtureRoot({
		source: `const label = t("pages.chat.absent", "Gone");`,
		en: { pages: { chat: { send: "Send" } } },
	});
	const options = { sourceRoot: join(root, "src"), bundlePath: join(root, "src", "locales", "en.json"), root };
	const previous = process.env.XE_I18N_MISSING_KEYS;

	try {
		// The default the CLI run resolves with an unset env: a missing key is an error, so the run exits non-zero.
		delete process.env.XE_I18N_MISSING_KEYS;
		const failed = checkI18nDefaults(options);
		assert.deepEqual(failed.drifted, []);
		assert.equal(failed.missing.length, 1);
		assert.match(failed.missing[0], /Widget\.tsx:1 pages\.chat\.absent/);
		assert.equal(failed.failOnMissing, true);

		// The escape hatch, for a deliberate work-in-progress run only.
		process.env.XE_I18N_MISSING_KEYS = "warn";
		assert.equal(checkI18nDefaults(options).failOnMissing, false);
	} finally {
		if (previous === undefined) {
			delete process.env.XE_I18N_MISSING_KEYS;
		} else {
			process.env.XE_I18N_MISSING_KEYS = previous;
		}
	}

	// An explicit argument still overrides the environment in both directions.
	assert.equal(checkI18nDefaults({ ...options, failOnMissing: false }).failOnMissing, false);
	assert.equal(checkI18nDefaults({ ...options, failOnMissing: true }).failOnMissing, true);
});

// The gate reads en.json alone, so nothing else would notice a key added to English and forgotten in German: the
// operator would then read English in a German UI, silently.
test("the shipped en and de bundles hold exactly the same keys", () => {
	const keysOf = (locale) =>
		new Set(Object.keys(flattenBundle(JSON.parse(readFileSync(join(frontendRoot, "src/locales", locale), "utf8")))));
	const en = keysOf("en.json");
	const de = keysOf("de.json");

	assert.deepEqual(
		{ enOnly: [...en].filter((key) => !de.has(key)), deOnly: [...de].filter((key) => !en.has(key)) },
		{ enOnly: [], deOnly: [] },
	);
});

test("the guard walks the real src tree and finds no drifted default and no missing key", () => {
	const { files, drifted, missing } = checkI18nDefaults();
	assert.ok(files > 500);
	assert.deepEqual(drifted, []);
	assert.deepEqual(missing, []);
});

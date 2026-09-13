// Fails the build when a `t("key", "default")` in-code default disagrees with src/locales/en.json.
//
// In the app the bundle always wins, so a drifted default is invisible there. It is NOT invisible in tests: a test
// file that never imports `@/i18n` renders react-i18next's uninitialised fallback, which echoes the in-code default
// verbatim and never interpolates. Such a test then asserts a sentence no operator ever sees. Keeping the two in
// agreement is what makes those assertions mean something.
//
// A key absent from en.json is a different defect and fails here too, for every `t()` call with a literal key —
// whether or not it carries a default. With a default the bundle has nothing to fall back to, so every locale renders
// the English in-code default; WITHOUT one there is nothing to render at all and the operator reads the raw dotted
// key. `XE_I18N_MISSING_KEYS=warn` downgrades the absent-key failure to a warning for a deliberate work-in-progress
// run; it is never the state a change lands in.
//
// Drift is checked only where a default exists, because there is nothing to compare otherwise.

import { readdirSync, readFileSync } from "node:fs";
import { createRequire } from "node:module";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(scriptDirectory, "..");
const ts = createRequire(join(frontendRoot, "package.json"))("typescript");

// Tests are excluded deliberately: a test's own `t()` default is fixture text, not shipped copy. The generated
// client and the generated route tree are never hand-edited, so drift there would be unfixable in place.
const excluded = [/\.test\.tsx?$/, /[\\/]test[\\/]/, /[\\/]core[\\/]api[\\/]generated[\\/]/, /[\\/]routeTree\.gen\.ts$/];

// i18next stores a pluralised key under suffixed siblings; the bare key it is called with never exists on its own.
const pluralSuffixes = ["_zero", "_one", "_two", "_few", "_many", "_other"];

/**
 * The bundle as one dotted-key map. The accumulator has a null prototype on purpose: a plain `{}` would answer
 * `"constructor" in bundle` — and `toString`, `valueOf`, … — with true, so a key named after an Object.prototype
 * member would look present and its default would be compared against a function.
 */
export function flattenBundle(value, prefix = "", out = Object.create(null)) {
	for (const [key, child] of Object.entries(value)) {
		const path = prefix ? `${prefix}.${key}` : key;
		if (child !== null && typeof child === "object" && !Array.isArray(child)) {
			flattenBundle(child, path, out);
		} else {
			out[path] = child;
		}
	}
	return out;
}

/** The bundle value for a key, following the plural convention. `undefined` means the key is absent entirely. */
function bundleValue(bundle, key) {
	if (key in bundle) {
		return bundle[key];
	}
	// A plural family agrees with the code default when ANY of its forms does: `_other` is the canonical default to
	// write in code, but `_one` is the right literal for a call site that is always singular.
	const forms = pluralSuffixes.map((suffix) => bundle[`${key}${suffix}`]).filter((value) => value !== undefined);
	return forms.length === 0 ? undefined : forms;
}

const isTranslateCallee = (expression) =>
	(ts.isIdentifier(expression) && expression.text === "t") ||
	(ts.isPropertyAccessExpression(expression) && expression.name.text === "t");

const literalText = (node) =>
	node !== undefined && (ts.isStringLiteral(node) || ts.isNoSubstitutionTemplateLiteral(node)) ? node.text : undefined;

/**
 * Every `t()` call in one file whose key is a literal. `codeDefault` is the literal default when the call carries
 * one and `undefined` when it does not: such a call still asserts that the key exists, which is the half of the
 * guarantee that a default-less call site needs.
 */
export function findTranslationDefaults(source, fileName = "source.tsx") {
	const sourceFile = ts.createSourceFile(
		fileName,
		source,
		ts.ScriptTarget.Latest,
		true,
		fileName.endsWith(".tsx") ? ts.ScriptKind.TSX : ts.ScriptKind.TS,
	);
	const sites = [];

	function visit(node) {
		if (ts.isCallExpression(node) && isTranslateCallee(node.expression)) {
			const [keyArgument, defaultArgument] = node.arguments;
			const key = literalText(keyArgument);
			if (key !== undefined) {
				// Both call shapes: t(key, "default", …) and t(key, { defaultValue: "default", … }).
				let codeDefault = literalText(defaultArgument);
				if (codeDefault === undefined && defaultArgument !== undefined && ts.isObjectLiteralExpression(defaultArgument)) {
					for (const property of defaultArgument.properties) {
						if (ts.isPropertyAssignment(property) && property.name.getText(sourceFile) === "defaultValue") {
							codeDefault = literalText(property.initializer);
						}
					}
				}
				sites.push({ line: sourceFile.getLineAndCharacterOfPosition(node.getStart(sourceFile)).line + 1, key, codeDefault });
			}
		}
		ts.forEachChild(node, visit);
	}

	visit(sourceFile);
	return sites;
}

/**
 * Splits one file's call sites into drifted defaults and keys the bundle does not have. Every literal key is checked
 * for presence; drift is only meaningful where the call carries a default to disagree with.
 */
export function findI18nDefaultDrift(source, bundle, fileName = "source.tsx") {
	const drifted = [];
	const missing = [];
	for (const site of findTranslationDefaults(source, fileName)) {
		const value = bundleValue(bundle, site.key);
		if (value === undefined) {
			missing.push(site);
			continue;
		}
		if (site.codeDefault === undefined) {
			continue;
		}
		if (Array.isArray(value) ? !value.includes(site.codeDefault) : value !== site.codeDefault) {
			drifted.push({ ...site, bundleValue: Array.isArray(value) ? value.join(" | ") : value });
		}
	}
	return { drifted, missing };
}

function sourceFilesUnder(directory) {
	return readdirSync(directory, { recursive: true, withFileTypes: true })
		.filter((entry) => entry.isFile() && /\.tsx?$/.test(entry.name))
		.map((entry) => join(entry.parentPath, entry.name))
		.filter((file) => !excluded.some((pattern) => pattern.test(file)))
		.sort();
}

export function checkI18nDefaults({
	sourceRoot = resolve(frontendRoot, "src"),
	bundlePath = resolve(frontendRoot, "src/locales/en.json"),
	failOnMissing = process.env.XE_I18N_MISSING_KEYS !== "warn",
	root = frontendRoot,
} = {}) {
	const bundle = flattenBundle(JSON.parse(readFileSync(bundlePath, "utf8")));
	const files = sourceFilesUnder(sourceRoot);
	const drifted = [];
	const missing = [];
	for (const file of files) {
		const result = findI18nDefaultDrift(readFileSync(file, "utf8"), bundle, file);
		const at = (site) => `${relative(root, file)}:${site.line}`;
		drifted.push(
			...result.drifted.map(
				(site) => `${at(site)} ${site.key}\n    code:   ${site.codeDefault}\n    bundle: ${site.bundleValue}`,
			),
		);
		missing.push(...result.missing.map((site) => `${at(site)} ${site.key}`));
	}
	return { files: files.length, drifted, missing, failOnMissing };
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	const { files, drifted, missing, failOnMissing } = checkI18nDefaults();
	if (missing.length > 0) {
		// A warning run stays a one-liner: the list is long today and would bury the drift failures below it.
		if (failOnMissing) {
			process.stderr.write(`CheckI18nDefaults: ERROR — ${missing.length} t() key(s) absent from en.json:\n`);
			for (const entry of missing) {
				process.stderr.write(`  ${entry}\n`);
			}
		} else {
			process.stderr.write(
				`CheckI18nDefaults: WARNING — ${missing.length} t() key(s) are absent from en.json (XE_I18N_MISSING_KEYS=warn is set; unset it to list them and fail on them).\n`,
			);
		}
	}
	if (drifted.length > 0) {
		process.stderr.write(`\nCheckI18nDefaults: ${drifted.length} in-code default(s) disagree with src/locales/en.json:\n`);
		for (const entry of drifted) {
			process.stderr.write(`  ${entry}\n`);
		}
		process.stderr.write(
			"\nFix: make the two agree — adopt the bundle string in code, or update en.json (and de.json) to the code's wording.\n",
		);
	}
	if (drifted.length > 0 || (failOnMissing && missing.length > 0)) {
		process.exit(1);
	}
	process.stdout.write(
		`CheckI18nDefaults: OK — every literal t() key in ${files} files exists in en.json, and every in-code default matches it.\n`,
	);
}

import { runGit } from "./GitEnvironment.mjs";
import { runChecked } from "./RunPackageTool.mjs";
import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdirSync, mkdtempSync, readdirSync, readFileSync, rmSync, statSync, writeFileSync } from "node:fs";
import { createRequire } from "node:module";
import { tmpdir } from "node:os";
import { dirname, join, resolve } from "node:path";
import test from "node:test";
import { isDeepStrictEqual } from "node:util";

const RULE = "noExcessiveLinesPerFile";
// Biome's Node entry, run by this Node without a shell: through the pnpm shim Windows needs cmd.exe, which splits a
// --config-path holding a space (a temporary directory under a user profile).
const BIOME_ENTRY = createRequire(import.meta.url).resolve("@biomejs/biome/bin/biome");
// Every production source the rule covers, and the only files it may leave out: tests, test support, generated code.
const PRODUCTION_SOURCES = ["src/**/*.ts", "src/**/*.tsx"];
// The production override's limit may only go down.
const MAX_LINES_CEILING = 500;
const DOCUMENTED_EXCLUSIONS = [
	"!src/**/*.test.ts",
	"!src/**/*.test.tsx",
	"!src/test/**",
	"!src/**/test/**",
	"!src/core/api/generated/**",
	"!src/routeTree.gen.ts",
];

function readBiomeConfig() {
	return JSON.parse(readFileSync(resolve(process.cwd(), "biome.json"), "utf8"));
}

const isObject = (value) => value !== null && typeof value === "object" && !Array.isArray(value);

// The rule's own setting in a configuration section (a level string or { level, options }), or undefined.
function ruleSetting(section) {
	const style = section?.linter?.rules?.style;
	return isObject(style) ? style[RULE] : undefined;
}

function unexpected(section, allowed, prefix) {
	return Object.keys(isObject(section) ? section : {})
		.filter((key) => !allowed.includes(key))
		.map((key) => `sets ${prefix}${key}`);
}

// Group-wide switches that can turn the rule off without naming it, and the rule itself.
function ruleSwitches(rules) {
	const style = "style" in rules ? rules.style : {};
	const found = ["recommended", "preset"].filter((key) => key in rules).map((key) => `sets linter.rules.${key}`);
	if (!isObject(style)) {
		return [...found, "sets the style group"];
	}
	return [...found, ...["recommended", RULE].filter((key) => key in style).map((key) => `sets linter.rules.style.${key}`)];
}

// The one allowed non-off setting: error level with an integer maxLines (skipBlankLines optional).
function isProductionRule(setting) {
	if (
		!isObject(setting) ||
		setting.level !== "error" ||
		Object.keys(setting).some((key) => !["level", "options"].includes(key))
	) {
		return false;
	}
	const options = setting.options ?? {};
	return Number.isInteger(options.maxLines) && Object.keys(options).every((key) => ["maxLines", "skipBlankLines"].includes(key));
}

const isOffList = (override) =>
	isDeepStrictEqual(override, { includes: override.includes, linter: { rules: { style: { [RULE]: "off" } } } });

// biome.json is checked closed-world, never modelled: the only shapes allowed to affect linting a production source
// are the top-level linter enabled with its includes and rules (preset none), files and javascript without linting
// switches, exactly one override setting the rule at error, at most one off-list override naming only the rule, and
// other overrides holding includes, javascript globals and rules that switch no group and name no rule.
// scripts/ratchet-baseline-check.py (biome_shape_violations) rejects the same shapes.
function shapeViolations(config) {
	const linter = config.linter ?? {};
	const rules = Object.fromEntries(
		Object.entries(linter.rules ?? {}).filter(([key, value]) => key !== "preset" || value !== "none"),
	);
	const found = "extends" in config ? ["extends another configuration"] : [];
	if (("enabled" in linter ? linter.enabled : true) !== true) {
		found.push("linter.enabled is not true");
	}
	found.push(
		...unexpected(linter, ["enabled", "includes", "rules"], "linter."),
		...unexpected(config.files, ["ignoreUnknown", "includes"], "files."),
		...unexpected(config.javascript, ["formatter", "globals"], "javascript."),
		...ruleSwitches(rules).map((problem) => `top level: ${problem}`),
		...["files", "linter"].flatMap((section) =>
			(config[section]?.includes ?? [])
				.filter((pattern) => !isModelledInclude(pattern))
				.map((pattern) => `${section}.includes: ${JSON.stringify(pattern)} is not an include shape the guards model`),
		),
	);
	let production = 0;
	let offLists = 0;
	for (const [index, override] of (config.overrides ?? []).entries()) {
		if (isOffList(override)) {
			offLists += 1;
			continue;
		}
		const overrideRules = override.linter?.rules ?? {};
		let style = overrideRules.style;
		const problems = [
			...unexpected(override, ["includes", "linter", "javascript"], ""),
			...unexpected(override.linter, ["rules"], "linter."),
			...unexpected(override.javascript, ["globals"], "javascript."),
		];
		if (isProductionRule(ruleSetting(override))) {
			production += 1;
			style = Object.fromEntries(Object.entries(style).filter(([key]) => key !== RULE));
		}
		problems.push(...ruleSwitches("style" in overrideRules ? { ...overrideRules, style } : overrideRules));
		found.push(...problems.map((problem) => `overrides[${index}] (${(override.includes ?? []).join(", ")}): ${problem}`));
	}
	if (production !== 1) {
		found.push(`${production} overrides set ${RULE} at error with maxLines; exactly one may`);
	}
	if (offLists > 1) {
		found.push(`${offLists} off-list overrides; at most one may`);
	}
	return found;
}

function productionOverride(config = readBiomeConfig()) {
	const override = config.overrides.find((candidate) => isProductionRule(ruleSetting(candidate)));
	return { rule: ruleSetting(override), includes: override.includes ?? [] };
}

function offList(config = readBiomeConfig()) {
	return config.overrides.filter(isOffList).flatMap((override) => override.includes ?? []);
}

// A production source the rule misses, or an exclusion beyond the documented ones, lets an over-long file pass.
function coverageGaps(ruleIncludes) {
	return [
		...PRODUCTION_SOURCES.filter((source) => !ruleIncludes.includes(source)),
		...ruleIncludes.filter((include) => include.startsWith("!") && !DOCUMENTED_EXCLUSIONS.includes(include)),
	];
}

// A top-level include segment the guards model: literal path characters with `*` inside it, or `**` alone.
const INCLUDE_SEGMENT = /^(?:\*\*|(?:[A-Za-z0-9_.-]|\*(?!\*))+)$/;

// A top-level files/linter include has a shape walked() matches as Biome does: an optional ! or !!, then segments of
// INCLUDE_SEGMENT, never ./.., empty or a leading /; a trailing / only negated. Braces, ?, classes and escapes are left
// out (Biome rejects the last three). scripts/ratchet-baseline-check.py (modelled_include) accepts the same shapes.
function isModelledInclude(pattern) {
	if (typeof pattern !== "string") {
		return false;
	}
	const negated = pattern.startsWith("!");
	const body = pattern.replace(/^!!?/, "");
	return (negated ? body.replace(/\/$/, "") : body)
		.split("/")
		.every((segment) => INCLUDE_SEGMENT.test(segment) && segment !== "." && segment !== "..");
}

// A modelled include as Biome matches it: * within one segment, ** any number of segments (a/** never matches a), dot
// names included (node:path matchesGlob skips them).
function includeRegex(body) {
	const segments = body.split("/");
	const last = segments.length - 1;
	const literal = (text) => text.replace(/[.*+?^${}()|[\]\\]/g, "\\$&");
	const parts = segments.map((segment, index) => {
		if (segment === "**") {
			return index === last ? ".*" : "(?:[^/]+/)*";
		}
		return segment.split("*").map(literal).join("[^/]*") + (index === last ? "" : "/");
	});
	// s: a name may hold a line break.
	return new RegExp(`^${parts.join("")}$`, "s");
}

// Biome lints a path when a positive top-level include (absent: every file) matches it and no negated one (! or !!, a
// trailing / dropped) matches it or a directory above it: an excluded directory's whole subtree is skipped. Accepted
// ceiling: a path re-included after an exclusion counts as excluded, stricter than Biome's last-match order.
function walked(path, includes) {
	if (includes === undefined) {
		return true;
	}
	const parts = path.split("/");
	const walk = parts.map((_, index) => parts.slice(0, index + 1).join("/"));
	const positive = includes.filter((pattern) => !pattern.startsWith("!")).map(includeRegex);
	const negated = includes
		.filter((pattern) => pattern.startsWith("!"))
		.map((pattern) => includeRegex(pattern.replace(/^!+/, "").replace(/\/$/, "")));
	return positive.some((regex) => regex.test(path)) && !negated.some((regex) => walk.some((directory) => regex.test(directory)));
}

// Type declarations (*.d.ts) hold no runtime code; the top-level linter settings leave them out.
function productionSources(directory = process.cwd()) {
	const exclusions = DOCUMENTED_EXCLUSIONS.map((pattern) => pattern.slice(1));
	return readdirSync(resolve(directory, "src"), { recursive: true })
		.map((entry) => `src/${entry.replaceAll("\\", "/")}`)
		.filter((path) => PRODUCTION_SOURCES.some((pattern) => includeRegex(pattern).test(path)) && !path.endsWith(".d.ts"))
		.filter((path) => !exclusions.some((pattern) => includeRegex(pattern).test(path)));
}

// A production source the top-level files or linter includes leave out is never linted.
function unlintedSources(config, sources = productionSources()) {
	return sources.filter((path) => !(walked(path, config.files?.includes) && walked(path, config.linter?.includes)));
}

// A nested biome.json or biome.jsonc with root: false can switch linting off for its subtree, and Biome skips what a
// .ignore file lists (git does not read it), so the project root's biome.json is the only such file allowed.
// node_modules is never linted.
function nestedBiomeConfigs(directory = process.cwd(), relative = "") {
	return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
		const path = `${relative}${entry.name}`;
		if (entry.isDirectory()) {
			return entry.name === "node_modules" ? [] : nestedBiomeConfigs(join(directory, entry.name), `${path}/`);
		}
		return /^(biome\.jsonc?|\.ignore)$/.test(entry.name) && path !== "biome.json" ? [path] : [];
	});
}

// Biome's vcs.useIgnoreFile skips a file the .gitignore files ignore, even a tracked one, so git judges every tracked
// TypeScript source under src/ (runGit hides the caller's global excludes, as scripts/ratchet-baseline-check.py does).
function gitIgnoredSources(directory = process.cwd()) {
	const git = (args, input) => runGit(args, { allowedStatuses: [0, 1], cwd: directory, input }).stdout;
	const tracked = git(["ls-files", "-z", "--", "src"])
		.split("\0")
		.filter((path) => /\.tsx?$/.test(path));
	const ignored = git(["check-ignore", "--no-index", "-z", "--stdin"], tracked.join("\0"));
	return ignored.split("\0").filter(Boolean);
}

// Biome skips a file above files.maxSize (1 MiB unless set; the shape check rejects setting it) with only a warning,
// so a production source that large is never judged by the rule.
const BIOME_DEFAULT_MAX_SIZE = 1024 * 1024;

function oversizedSources(config, directory = process.cwd()) {
	const maxSize = config.files?.maxSize ?? BIOME_DEFAULT_MAX_SIZE;
	return productionSources(directory).filter((path) => statSync(resolve(directory, path)).size > maxSize);
}

// A suppression comment (line, range or file-wide) naming the rule, its style group or the whole lint group would
// exempt a file without an off-list row. Matched anywhere on a line, in any case, so no spelling Biome reads slips by;
// scripts/ratchet-baseline-check.py (SUPPRESSION) rejects the same comments.
const SUPPRESSION = new RegExp(
	`biome-ignore(?:-all|-start)?(?:[ \\t]+[^ \\t:]+)*[ \\t]+lint(?:/style(?:/${RULE})?)?(?:\\([^)]*\\))?(?:[ \\t:*]|$)`,
	"im",
);

function suppressedSources(directory = process.cwd()) {
	return productionSources(directory).filter((path) => SUPPRESSION.test(readFileSync(resolve(directory, path), "utf8")));
}

function isFile(entry) {
	return statSync(resolve(process.cwd(), entry), { throwIfNoEntry: false })?.isFile() ?? false;
}

// Biome counts the lines itself (comment-only lines do not count), so the off-list is judged by Biome with the rule
// forced on, never by a second line counter that could disagree with it.
function filesStillOverTheLimit(rule, files, { spawn = spawnSync, temporaryRoot = tmpdir() } = {}) {
	const configDirectory = mkdtempSync(join(temporaryRoot, "biome-file-length-"));
	try {
		writeFileSync(join(configDirectory, "biome.json"), JSON.stringify({ linter: { rules: { style: { [RULE]: rule } } } }));
		const result = runChecked(
			process.execPath,
			[BIOME_ENTRY, "lint", `--config-path=${configDirectory}`, `--only=style/${RULE}`, "--reporter=json", ...files],
			{ allowedStatuses: [0, 1], spawn },
		);
		const report = JSON.parse(result.stdout);
		return new Set(
			report.diagnostics
				.filter((diagnostic) => diagnostic.category === `lint/style/${RULE}`)
				.map((diagnostic) => diagnostic.location.path),
		);
	} finally {
		rmSync(configDirectory, { recursive: true, force: true });
	}
}

// The off-list names files, never a glob or directory. A fully paid-off tree has no off-list left to judge, so Biome
// runs only while file exemptions remain.
function staleOffListEntries(rule, offList, judge = filesStillOverTheLimit) {
	const files = offList.filter(isFile);
	const over = files.length === 0 ? new Set() : judge(rule, files);
	return offList.filter((entry) => !over.has(entry));
}

test("the noExcessiveLinesPerFile off-list names only files still over the limit (shrink-only)", () => {
	const config = readBiomeConfig();
	assert.deepEqual(shapeViolations(config), [], "biome.json may affect linting production sources only in the allowed shapes.");
	const { rule, includes } = productionOverride(config);
	assert.ok(rule.options.maxLines <= MAX_LINES_CEILING, `${RULE} maxLines must stay at most ${MAX_LINES_CEILING}.`);
	assert.deepEqual(unlintedSources(config), [], "biome.json's top-level includes must select every production source.");
	assert.deepEqual(
		coverageGaps(includes),
		[],
		`${RULE} must cover ${PRODUCTION_SOURCES} with only test and generated exclusions.`,
	);
	assert.deepEqual(nestedBiomeConfigs(), [], "biome.json at the project root must be the only Biome configuration.");
	assert.deepEqual(gitIgnoredSources(), [], "Biome skips a tracked source git ignores: un-ignore it.");
	assert.deepEqual(oversizedSources(config), [], "Biome skips a source above files.maxSize: split it.");
	assert.deepEqual(suppressedSources(), [], `A source-level suppression exempts a file from ${RULE}: split the file.`);

	assert.deepEqual(
		staleOffListEntries(rule, offList(config)),
		[],
		`These entries are no file over ${rule.options.maxLines} lines: delete them from the ${RULE} off-list in biome.json.`,
	);
});

test("an empty noExcessiveLinesPerFile off-list passes without running Biome", () => {
	const judge = () => {
		throw new Error("Biome ran for an empty off-list.");
	};
	assert.deepEqual(staleOffListEntries({ options: { maxLines: 500 } }, [], judge), []);
});

test("Biome runs without a shell and receives a config path holding a space as one argument", () => {
	const temporaryRoot = mkdtempSync(join(tmpdir(), "biome root with space-"));
	const calls = [];
	const spawn = (command, args, options) => {
		calls.push({ command, args, options });
		return spawnSync(command, args, options);
	};
	const file = "scripts/BiomeFileLengthOffList.test.mjs";
	try {
		const over = filesStillOverTheLimit({ level: "error", options: { maxLines: 10 } }, [file], { spawn, temporaryRoot });

		assert.deepEqual([...over], [file]);
		assert.equal(calls.length, 1);
		assert.equal(calls[0].command, process.execPath);
		assert.equal(calls[0].options.shell, false);
		const configPaths = calls[0].args.filter((argument) => argument.startsWith("--config-path="));
		assert.equal(configPaths.length, 1);
		assert.ok(configPaths[0].startsWith(`--config-path=${join(temporaryRoot, "biome-file-length-")}`));
	} finally {
		rmSync(temporaryRoot, { recursive: true, force: true });
	}
});

// Every shape outside the closed world is rejected, whatever Biome would make of it.
test("a Biome setting outside the allowed shapes is rejected", () => {
	const lab = ["src/features/**"];
	const limit = (maxLines) => ({ linter: { rules: { style: { [RULE]: { level: "error", options: { maxLines } } } } } });
	const withOverride = (override) => {
		const config = readBiomeConfig();
		config.overrides.push({ includes: lab, ...override });
		return shapeViolations(config);
	};
	const at = (problem) => [`overrides[${readBiomeConfig().overrides.length}] (src/features/**): ${problem}`];
	const twoRules = [`2 overrides set ${RULE} at error with maxLines; exactly one may`];

	assert.deepEqual(shapeViolations(readBiomeConfig()), []);
	assert.deepEqual(withOverride({ javascript: { linter: { enabled: false } } }), at("sets javascript.linter"));
	assert.deepEqual(withOverride({ linter: { enabled: false } }), at("sets linter.enabled"));
	assert.deepEqual(withOverride({ files: { maxSize: 10 } }), at("sets files"));
	assert.deepEqual(withOverride({ linter: { rules: { style: "off" } } }), at("sets the style group"));
	assert.deepEqual(withOverride({ linter: { rules: { recommended: false, style: { recommended: false } } } }), [
		...at("sets linter.rules.recommended"),
		...at("sets linter.rules.style.recommended"),
	]);
	assert.deepEqual(
		withOverride({ linter: { rules: { style: { [RULE]: { level: "off", options: { maxLines: 0 } } } } } }),
		at(`sets linter.rules.style.${RULE}`),
	);
	assert.deepEqual(withOverride({ linter: { rules: { style: { [RULE]: "warn" } } } }), at(`sets linter.rules.style.${RULE}`));
	assert.deepEqual(withOverride(limit(300)), twoRules);
	assert.deepEqual(withOverride(limit(450)), twoRules);
	assert.deepEqual(withOverride(limit(900)), twoRules);

	const downgraded = readBiomeConfig();
	const production = downgraded.overrides.find((override) => isProductionRule(ruleSetting(override)));
	production.linter.rules.style[RULE].level = "warn";
	assert.deepEqual(shapeViolations(downgraded).at(-1), `0 overrides set ${RULE} at error with maxLines; exactly one may`);

	const top = { ...readBiomeConfig(), extends: ["./other.json"], files: { maxSize: 10 }, javascript: { linter: {} } };
	top.linter = { ...top.linter, enabled: false, rules: { ...top.linter.rules, style: { [RULE]: "info" } } };
	assert.deepEqual(shapeViolations(top), [
		"extends another configuration",
		"linter.enabled is not true",
		"sets files.maxSize",
		"sets javascript.linter",
		`top level: sets linter.rules.style.${RULE}`,
	]);
});

test("the installed Biome lets an oversized component through the rejected shapes and flags it under the real one", () => {
	const root = mkdtempSync(join(tmpdir(), "biome-shape-"));
	try {
		const page = join(root, "src", "features", "lab", "LabPage.tsx");
		mkdirSync(dirname(page), { recursive: true });
		writeFileSync(page, "export const line = 1;\n".repeat(600));
		const lint = (...overrides) => {
			// Without vcs the scratch tree needs no git repository; the linting settings stay as committed.
			const { vcs: _vcs, ...config } = readBiomeConfig();
			config.overrides.push(...overrides);
			writeFileSync(join(root, "biome.json"), JSON.stringify(config));
			const result = runChecked(process.execPath, [BIOME_ENTRY, "lint", `--only=style/${RULE}`, "--reporter=json", "src"], {
				allowedStatuses: [0, 1],
				cwd: root,
			});
			const flagged = JSON.parse(result.stdout).diagnostics.some((diagnostic) => diagnostic.category === `lint/style/${RULE}`);
			return { flagged, violations: shapeViolations(config).length };
		};
		const lab = ["src/features/**"];

		assert.deepEqual(lint(), { flagged: true, violations: 0 });
		assert.deepEqual(lint({ includes: lab, javascript: { linter: { enabled: false } } }), { flagged: false, violations: 1 });
		const later = { includes: lab, linter: { rules: { style: { [RULE]: { level: "error", options: { maxLines: 900 } } } } } };
		assert.deepEqual(lint(later), { flagged: false, violations: 1 });
	} finally {
		rmSync(root, { recursive: true, force: true });
	}
});

test("a production source above Biome's size limit is skipped by Biome and caught", () => {
	const root = mkdtempSync(join(tmpdir(), "biome-oversized-"));
	try {
		const page = "src/features/lab/LabPage.tsx";
		mkdirSync(join(root, "src", "features", "lab"), { recursive: true });
		// 600 code lines of about 1.9 KB each: over 500 lines and over 1 MiB.
		writeFileSync(join(root, page), `export const line = 1; // ${"x".repeat(1900)}\n`.repeat(600));
		const { vcs: _vcs, ...config } = readBiomeConfig();
		writeFileSync(join(root, "biome.json"), JSON.stringify(config));
		const result = runChecked(process.execPath, [BIOME_ENTRY, "lint", `--only=style/${RULE}`, "--reporter=json", "src"], {
			allowedStatuses: [0, 1],
			cwd: root,
		});
		const flagged = JSON.parse(result.stdout).diagnostics.some((diagnostic) => diagnostic.category === `lint/style/${RULE}`);

		assert.deepEqual([flagged, oversizedSources(config, root)], [false, [page]]);
		writeFileSync(join(root, page), "export const line = 1;\n".repeat(600));
		assert.deepEqual(oversizedSources(config, root), []);
	} finally {
		rmSync(root, { recursive: true, force: true });
	}
});

test("an off-list exemption moved into a source suppression is skipped by Biome and caught", () => {
	const root = mkdtempSync(join(tmpdir(), "biome-suppressed-"));
	try {
		const page = "src/features/lab/LabPage.tsx";
		mkdirSync(join(root, "src", "features", "lab"), { recursive: true });
		const body = "export const line = 1;\n".repeat(600);
		const { vcs: _vcs, ...config } = readBiomeConfig();
		writeFileSync(join(root, "biome.json"), JSON.stringify(config));
		const lint = (text) => {
			writeFileSync(join(root, page), text);
			const result = runChecked(process.execPath, [BIOME_ENTRY, "lint", `--only=style/${RULE}`, "--reporter=json", "src"], {
				allowedStatuses: [0, 1],
				cwd: root,
			});
			const report = JSON.parse(result.stdout);
			return [report.diagnostics.some((diagnostic) => diagnostic.category === `lint/style/${RULE}`), suppressedSources(root)];
		};

		assert.deepEqual(lint(body), [true, []]);
		assert.deepEqual(lint(`// biome-ignore-all lint/style/${RULE}: moved from the off-list\n${body}`), [false, [page]]);
		for (const comment of [
			"// biome-ignore-all lint: reason",
			"// biome-ignore-all lint/style: reason",
			"/* biome-ignore-start lint/correctness/noUnusedVariables lint/style: reason */",
			`{/* biome-ignore lint/style/${RULE}(x): reason */}`,
			`// BIOME-IGNORE-ALL LINT/STYLE/${RULE.toUpperCase()}: reason`,
		]) {
			assert.deepEqual(lint(`${comment}\nexport const line = 1;\n`)[1], [page], comment);
		}
		for (const comment of [
			"// biome-ignore-all lint/style/useNamingConvention: reason",
			"// biome-ignore lint/suspicious/noArrayIndexKey: reason",
			"// biome-ignore lint/styles: reason",
		]) {
			assert.deepEqual(lint(`${comment}\nexport const line = 1;\n`)[1], [], comment);
		}
	} finally {
		rmSync(root, { recursive: true, force: true });
	}
});

test("narrowing the production noExcessiveLinesPerFile override is caught with its limit unchanged", () => {
	const narrowed = (edit) => {
		const config = readBiomeConfig();
		const override = config.overrides.find((candidate) => isProductionRule(ruleSetting(candidate)));
		override.includes = edit(override.includes);
		return productionOverride(config);
	};
	const withoutTsx = narrowed((includes) => includes.filter((include) => include !== "src/**/*.tsx"));
	const withExclusion = narrowed((includes) => [...includes, "!src/features/**"]);

	assert.equal(withoutTsx.rule.options.maxLines, 500);
	assert.deepEqual(coverageGaps(withoutTsx.includes), ["src/**/*.tsx"]);
	assert.deepEqual(coverageGaps(withExclusion.includes), ["!src/features/**"]);
	assert.deepEqual(coverageGaps(productionOverride().includes), []);
});

test("a top-level include or exclusion that stops linting a production source is caught", () => {
	const sources = ["src/Main.tsx", "src/core/api/Client.ts", "src/features/chat/ChatPage.tsx"];
	const withTop = (section, includes) => ({ ...readBiomeConfig(), [section]: { includes } });

	assert.ok(productionSources().length > 0, "no production source found under src/.");
	assert.deepEqual(unlintedSources(withTop("files", ["src/**/*.ts"]), sources), [
		"src/Main.tsx",
		"src/features/chat/ChatPage.tsx",
	]);
	assert.deepEqual(unlintedSources(withTop("linter", ["**", "!src/features/**"]), sources), ["src/features/chat/ChatPage.tsx"]);
	assert.deepEqual(unlintedSources(withTop("files", ["**", "!**/pnpm-lock.yaml"]), sources), []);
	assert.deepEqual(unlintedSources(withTop("linter", ["**"]), ["src/features/line\nbreak/Page.tsx", "src/Größe.tsx"]), []);
});

test("a directory the top-level includes exclude is skipped by Biome with its subtree and caught", () => {
	const root = mkdtempSync(join(tmpdir(), "biome-walk-"));
	try {
		const sources = [
			"src/features/modelLab/ModelLabPage.tsx",
			"src/features/modelLab/deep/Deep.tsx",
			"src/features/chat/ChatPage.tsx",
			"src/.hidden/Dot.tsx",
		];
		for (const source of sources) {
			mkdirSync(dirname(join(root, source)), { recursive: true });
			writeFileSync(join(root, source), "export const line = 1;\n".repeat(600));
		}
		const modelLab = sources.slice(0, 2);
		// What the installed Biome lints, run as the lint script runs it (no path: the whole project), and what the
		// guard judges linted, under the committed settings with one top-level includes list replaced.
		const lint = (section, includes) => {
			const { vcs: _vcs, ...committed } = readBiomeConfig();
			const config = includes === undefined ? committed : { ...committed, [section]: { includes } };
			writeFileSync(join(root, "biome.json"), JSON.stringify(config));
			const result = runChecked(process.execPath, [BIOME_ENTRY, "lint", `--only=style/${RULE}`, "--reporter=json"], {
				allowedStatuses: [0, 1],
				cwd: root,
			});
			const biome = JSON.parse(result.stdout)
				.diagnostics.filter((diagnostic) => diagnostic.category === `lint/style/${RULE}`)
				.map((diagnostic) => diagnostic.location.path)
				.sort();
			const unlinted = unlintedSources(config, sources);
			return { biome, guard: sources.filter((source) => !unlinted.includes(source)).sort(), violations: shapeViolations(config) };
		};
		const except = (...skipped) => sources.filter((source) => !skipped.includes(source)).sort();

		for (const [section, includes, linted] of [
			["linter", undefined, except()],
			["files", ["**", "!!src/features/modelLab"], except(...modelLab)],
			["files", ["**", "!src/features/modelLab"], except(...modelLab)],
			["linter", ["**", "!src/features/modelLab"], except(...modelLab)],
			["linter", ["**", "!**/modelLab"], except(...modelLab)],
			["linter", ["**", "!src/features/model*"], except(...modelLab)],
			["linter", ["**", "!src/features/*"], ["src/.hidden/Dot.tsx"]],
			["linter", ["**", "!src/features/modelLab/deep"], except(modelLab[1])],
			["linter", ["**", "!src/features/modelLab", modelLab[0]], except(...modelLab)],
			["linter", ["**", "!**/.hidden"], except("src/.hidden/Dot.tsx")],
			["linter", ["src/**"], except()],
			["linter", ["**/*.tsx"], except()],
			["linter", ["src/features"], []],
		]) {
			assert.deepEqual(
				lint(section, includes),
				{ biome: linted, guard: linted, violations: [] },
				JSON.stringify(includes ?? "committed"),
			);
		}
		// Accepted ceiling: the guard is stricter than Biome for a trailing-slash exclusion (Biome matches no directory
		// with it) and for a file re-included after its directory's contents were excluded.
		for (const includes of [
			["**", "!src/features/modelLab/"],
			["**", "!src/features/modelLab/**", modelLab[0]],
		]) {
			const { biome, guard, violations } = lint("linter", includes);
			assert.deepEqual(violations, [], JSON.stringify(includes));
			assert.ok(
				guard.every((source) => biome.includes(source)),
				JSON.stringify({ includes, biome, guard }),
			);
			assert.ok(biome.includes(modelLab[0]) && !guard.includes(modelLab[0]), JSON.stringify({ includes, biome, guard }));
		}
		// A shape outside the modelled ones is rejected, whatever Biome makes of it.
		for (const pattern of ["!src/features/{modelLab,chat}", "./src/**", "src/", "!src//features", "!!!src/features", "/src/**"]) {
			const config = { ...readBiomeConfig(), linter: { ...readBiomeConfig().linter, includes: ["**", pattern] } };
			assert.deepEqual(
				shapeViolations(config),
				[`linter.includes: ${JSON.stringify(pattern)} is not an include shape the guards model`],
				pattern,
			);
		}
	} finally {
		rmSync(root, { recursive: true, force: true });
	}
});

test("an off-list glob is stale and never reaches Biome", () => {
	const judged = [];
	const judge = (_rule, files) => {
		judged.push(...files);
		return new Set(files);
	};

	assert.deepEqual(staleOffListEntries(productionOverride().rule, ["src/features/**"], judge), ["src/features/**"]);
	assert.deepEqual(judged, []);
});

test("a nested Biome configuration beside an oversized component is caught", () => {
	const root = mkdtempSync(join(tmpdir(), "biome-nested-"));
	try {
		const lab = join(root, "src", "features", "modelLab");
		mkdirSync(join(root, "node_modules", "pkg"), { recursive: true });
		mkdirSync(lab, { recursive: true });
		writeFileSync(join(root, "biome.json"), "{}");
		writeFileSync(join(root, "node_modules", "pkg", "biome.json"), "{}");
		writeFileSync(join(lab, "ModelLabPage.tsx"), "export const line = 1;\n".repeat(600));
		assert.deepEqual(nestedBiomeConfigs(root), []);

		const nested = JSON.stringify({ root: false, linter: { enabled: false } });
		writeFileSync(join(lab, "biome.json"), nested);
		writeFileSync(join(root, "src", "biome.jsonc"), nested);
		writeFileSync(join(root, "biome.jsonc"), nested);
		writeFileSync(join(root, "src", "features", ".ignore"), "modelLab/\n");
		assert.deepEqual(nestedBiomeConfigs(root).sort(), [
			"biome.jsonc",
			"src/biome.jsonc",
			"src/features/.ignore",
			"src/features/modelLab/biome.json",
		]);
	} finally {
		rmSync(root, { recursive: true, force: true });
	}
});

function checkAGitignoredOversizedComponent() {
	const root = mkdtempSync(join(tmpdir(), "biome-gitignored-"));
	const lint = () => {
		const result = runChecked(process.execPath, [BIOME_ENTRY, "lint", `--only=style/${RULE}`, "--reporter=json", "src"], {
			allowedStatuses: [0, 1],
			cwd: root,
		});
		return JSON.parse(result.stdout).diagnostics.some((diagnostic) => diagnostic.category === `lint/style/${RULE}`);
	};
	try {
		const page = "src/features/lab/LabPage.tsx";
		mkdirSync(join(root, "src", "features", "lab"), { recursive: true });
		writeFileSync(join(root, page), "export const line = 1;\n".repeat(600));
		writeFileSync(join(root, "biome.json"), readFileSync(resolve(process.cwd(), "biome.json")));
		// Biome's vcs integration refuses to run without a non-empty ignore file; the init template supplies none.
		writeFileSync(join(root, ".gitignore"), "# no entries\n");
		runGit(["init", "-q"], { cwd: root });
		runGit(["add", "-A"], { cwd: root });
		assert.deepEqual([lint(), gitIgnoredSources(root)], [true, []]);

		writeFileSync(join(root, ".gitignore"), `${page}\n`);
		assert.deepEqual([lint(), gitIgnoredSources(root)], [false, [page]]);
		writeFileSync(join(root, ".gitignore"), "# no entries\n");
		writeFileSync(join(root, "src", "features", ".gitignore"), "lab/\n");
		assert.deepEqual([lint(), gitIgnoredSources(root)], [false, [page]]);
	} finally {
		rmSync(root, { recursive: true, force: true });
	}
}

test("a tracked oversized component that a .gitignore lists is skipped by Biome and caught", checkAGitignoredOversizedComponent);

test("the fixture's git leaves a private index the caller exported untouched", () => {
	const caller = mkdtempSync(join(tmpdir(), "biome-caller-index-"));
	const exported = Object.hasOwn(process.env, "GIT_INDEX_FILE") ? process.env.GIT_INDEX_FILE : undefined;
	try {
		writeFileSync(join(caller, "staged.txt"), "staged by the caller\n");
		runGit(["init", "-q"], { cwd: caller });
		runGit(["add", "staged.txt"], { cwd: caller });
		const index = join(caller, ".git", "index");
		const before = readFileSync(index);
		process.env.GIT_INDEX_FILE = index;
		try {
			checkAGitignoredOversizedComponent();
		} finally {
			if (exported === undefined) {
				delete process.env.GIT_INDEX_FILE;
			} else {
				process.env.GIT_INDEX_FILE = exported;
			}
		}
		assert.deepEqual(readFileSync(index), before);
	} finally {
		rmSync(caller, { recursive: true, force: true });
	}
});

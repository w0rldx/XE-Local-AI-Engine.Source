// Fails the build on a hand-rolled `<MantineProvider env="test">` in test code that does not pass `testMantineTheme`.
//
// `env="test"` only short-circuits what `<Transition>` RENDERS; `useTransition` runs above that check regardless, so
// an overlay whose default duration is non-zero schedules a real requestAnimationFrame plus setTimeout on every close
// and keeps it alive until the component hosting the Transition unmounts — which an overlay never does, it only
// toggles `mounted`. Under load that timer fires after `afterEach(cleanup)` and the vitest process exits 1 with every
// test in the file green. `testMantineTheme` (src/test/MantineTestRender.tsx) zeroes those durations, which is what
// actually makes the synchronous branch run. Nothing else in the toolchain can see the omission: the file type-checks,
// lints and passes.
//
// A test file that renders through `renderWithProviders`/`renderWithMantine`/`createProvidersWrapper` has no provider
// of its own and is therefore nothing to check.
//
// It also fails on a test file that declares its own `installJsdomEnvironmentMocks`, `renderWithMantine` or
// `renderWithProviders`: a local copy drifts from the shared one in src/test (copies lacked its `scrollIntoView` stub)
// and a same-named wrapper hides which providers the test really gets.

import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(scriptDirectory, "..");

const providerName = "MantineProvider";
const themeAttribute = "theme={testMantineTheme}";

/**
 * The text of the JSX opening tag starting at `start`, or undefined when it is unterminated. Quotes and `{}` holes
 * are tracked so a `>` inside an attribute value (`onX={(a) => b}`, `title="a > b"`) does not end the tag early.
 */
function openingTag(source, start) {
	let depth = 0;
	let quote = "";
	for (let cursor = start; cursor < source.length; cursor += 1) {
		const character = source[cursor];
		if (quote !== "") {
			if (character === quote) {
				quote = "";
			}
			continue;
		}
		if (character === '"' || character === "'" || character === "`") {
			quote = character;
			continue;
		}
		if (character === "{") {
			depth += 1;
			continue;
		}
		if (character === "}") {
			depth -= 1;
			continue;
		}
		if (character === ">" && depth === 0) {
			return source.slice(start, cursor + 1);
		}
	}
	return undefined;
}

/** Every `<MantineProvider …>` in one file, as `{ line, tag, isTestEnv, hasTheme }`. */
export function findMantineProviders(source) {
	const found = [];
	let index = source.indexOf(`<${providerName}`);
	while (index !== -1) {
		// `<MantineProviderSomething` is a different component.
		const after = source[index + providerName.length + 1];
		if (after !== undefined && !/[\w$]/.test(after)) {
			const tag = openingTag(source, index);
			if (tag !== undefined) {
				const collapsed = tag.replace(/\s+/g, " ");
				found.push({
					line: source.slice(0, index).split("\n").length,
					tag: collapsed,
					isTestEnv: /env=["']test["']/.test(collapsed),
					hasTheme: collapsed.includes(themeAttribute),
				});
			}
		}
		index = source.indexOf(`<${providerName}`, index + 1);
	}
	return found;
}

/** The providers in one file that declare `env="test"` without the shared theme. */
export function findUnthemedTestProviders(source) {
	return findMantineProviders(source).filter((provider) => provider.isTestEnv && !provider.hasTheme);
}

const sharedHelperDeclaration =
	/^[\t ]*(?:export\s+)?(?:(?:async\s+)?function\s+|(?:const|let|var)\s+)(installJsdomEnvironmentMocks|renderWithMantine|renderWithProviders)\b/gm;

/** Every local declaration of a shared src/test helper in one file, as `{ line, name }`. */
export function findSharedHelperCopies(source) {
	return [...source.matchAll(sharedHelperDeclaration)].map((match) => ({
		line: source.slice(0, match.index).split("\n").length,
		name: match[1],
	}));
}

const testFile = /\.test\.tsx?$/;
// The shared wrappers in src/test are held to the same rule: they are where the other 100+ files get the theme from.
const scanned = [testFile, /[\\/]test[\\/][^\\/]+\.tsx$/];

function candidateFilesUnder(directory) {
	return readdirSync(directory, { recursive: true, withFileTypes: true })
		.filter((entry) => entry.isFile() && scanned.some((pattern) => pattern.test(join(entry.parentPath, entry.name))))
		.map((entry) => join(entry.parentPath, entry.name))
		.sort();
}

export function checkMantineTestTheme({ sourceRoot = resolve(frontendRoot, "src"), root = frontendRoot } = {}) {
	const files = candidateFilesUnder(sourceRoot);
	let providers = 0;
	const offenders = [];
	const copies = [];
	for (const file of files) {
		const source = readFileSync(file, "utf8");
		if (testFile.test(file)) {
			for (const copy of findSharedHelperCopies(source)) {
				copies.push(`${relative(root, file)}:${copy.line}: ${copy.name}`);
			}
		}
		for (const provider of findMantineProviders(source)) {
			if (!provider.isTestEnv) {
				continue;
			}
			providers += 1;
			if (!provider.hasTheme) {
				offenders.push(`${relative(root, file)}:${provider.line}: ${provider.tag}`);
			}
		}
	}
	return { files: files.length, providers, offenders, copies };
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	const { files, providers, offenders, copies } = checkMantineTestTheme();
	// A scan that reached nothing would pass for free, and this rule has over a hundred live call sites.
	if (files === 0 || providers === 0) {
		process.stderr.write(
			`CheckMantineTestTheme: ERROR — scanned ${files} file(s) and found ${providers} <${providerName} env="test">; the scan is broken, not the code.\n`,
		);
		process.exit(1);
	}
	if (copies.length > 0) {
		process.stderr.write(`CheckMantineTestTheme: ${copies.length} local declaration(s) of a shared src/test helper:\n`);
		for (const entry of copies) {
			process.stderr.write(`  ${entry}\n`);
		}
		process.stderr.write(
			`\nFix: import installJsdomEnvironmentMocks/renderWithMantine from "@/test/MantineTestRender" or renderWithProviders/createProvidersWrapper from "@/test/RenderWithProviders"; a wrapper that adds something is named after what it adds.\n`,
		);
	}
	if (offenders.length > 0) {
		process.stderr.write(
			`CheckMantineTestTheme: ${offenders.length} hand-rolled <${providerName} env="test"> without ${themeAttribute}:\n`,
		);
		for (const entry of offenders) {
			process.stderr.write(`  ${entry}\n`);
		}
		process.stderr.write(
			`\nFix: add ${themeAttribute} (import it from "@/test/MantineTestRender"), or render through renderWithProviders/renderWithMantine instead.\n`,
		);
	}
	if (copies.length > 0 || offenders.length > 0) {
		process.exit(1);
	}
	process.stdout.write(
		`CheckMantineTestTheme: OK — all ${providers} hand-rolled <${providerName} env="test"> in ${files} files pass ${themeAttribute}; no test file declares its own shared helper.\n`,
	);
}

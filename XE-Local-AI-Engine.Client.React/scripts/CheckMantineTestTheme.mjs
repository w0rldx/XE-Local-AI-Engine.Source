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

// The shared wrappers in src/test are held to the same rule: they are where the other 100+ files get the theme from.
const scanned = [/\.test\.tsx$/, /[\\/]test[\\/][^\\/]+\.tsx$/];

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
	for (const file of files) {
		const source = readFileSync(file, "utf8");
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
	return { files: files.length, providers, offenders };
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	const { files, providers, offenders } = checkMantineTestTheme();
	// A scan that reached nothing would pass for free, and this rule has over a hundred live call sites.
	if (files === 0 || providers === 0) {
		process.stderr.write(
			`CheckMantineTestTheme: ERROR — scanned ${files} file(s) and found ${providers} <${providerName} env="test">; the scan is broken, not the code.\n`,
		);
		process.exit(1);
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
		process.exit(1);
	}
	process.stdout.write(
		`CheckMantineTestTheme: OK — all ${providers} hand-rolled <${providerName} env="test"> in ${files} files pass ${themeAttribute}.\n`,
	);
}

// Fails the build on a Vitest test that asserts nothing. Such a test passes for as long as its body does not throw,
// so it reports green on code it never checked — and Biome ships no `expect-expect` equivalent to catch it.
//
// An assertion counts when the block calls `expect(`/`expect.`, or any function whose name starts with `expect` or
// `assert` — helper-based assertions (`expectDocumentOrder(...)`, `assert.ok(...)`) are the house style, not a smell.

import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(scriptDirectory, "..");

const assertionPattern = /\b(?:expect|assert)\w*\s*[(.]/;
// The lookbehind is load-bearing: without it `SKILL_NAME_PATTERN.test(name)` reads as a test declaration.
const testCallPattern = /(?<![.\w$])(?:it|test)((?:\.\w+)*)\s*(?=[(`])/g;

const blank = (character) => (character === "\n" || character === "\r" ? character : " ");

/**
 * Blanks the CONTENT of comments, strings, templates and regex literals while keeping their delimiters, so brackets
 * inside them cannot skew the balanced matching and a test *named* "expects a thing" is not read as an assertion.
 */
function maskLiterals(source) {
	const output = source.split("");
	let previous = "";
	let index = 0;
	while (index < source.length) {
		const character = source[index];
		const next = source[index + 1];
		if (character === "/" && (next === "/" || next === "*")) {
			const lineComment = next === "/";
			index += 2;
			while (index < source.length) {
				if (lineComment ? source[index] === "\n" : source[index - 1] === "*" && source[index] === "/") {
					break;
				}
				output[index] = blank(source[index]);
				index += 1;
			}
			if (!lineComment && index < source.length) {
				output[index - 1] = " ";
				index += 1;
			}
			continue;
		}
		const quoted = character === '"' || character === "'" || character === "`";
		// A `/` after a value is division; after an operator, a bracket or nothing it opens a regex literal.
		const regex = character === "/" && (previous === "" || /[(,=:[!&|?{};+\-*%<>~^]/.test(previous));
		if (quoted || regex) {
			const terminator = quoted ? character : "/";
			index += 1;
			while (index < source.length) {
				const inner = source[index];
				if (inner === "\\") {
					output[index] = " ";
					output[index + 1] = blank(source[index + 1] ?? "");
					index += 2;
					continue;
				}
				if (inner === terminator || (regex && inner === "\n")) {
					break;
				}
				output[index] = blank(inner);
				index += 1;
			}
			previous = "x";
			index += 1;
			continue;
		}
		if (!/\s/.test(character)) {
			previous = character;
		}
		index += 1;
	}
	return output.join("");
}

/** Index of the bracket closing the group that opens at `start`, or -1 when the source is unbalanced. */
function closingIndex(masked, start) {
	if (masked[start] === "`") {
		return masked.indexOf("`", start + 1);
	}
	let depth = 0;
	for (let cursor = start; cursor < masked.length; cursor += 1) {
		if (masked[cursor] === "(") {
			depth += 1;
		} else if (masked[cursor] === ")") {
			depth -= 1;
			if (depth === 0) {
				return cursor;
			}
		}
	}
	return -1;
}

const nextGroup = (masked, from) => {
	let cursor = from;
	while (/\s/.test(masked[cursor] ?? "")) {
		cursor += 1;
	}
	return masked[cursor] === "(" || masked[cursor] === "`" ? cursor : -1;
};

/** Every `it`/`test` block in one file whose arguments contain no assertion, as `{ line, name }`. */
export function findTestsWithoutAssertions(source) {
	const masked = maskLiterals(source);
	const findings = [];
	for (const match of masked.matchAll(testCallPattern)) {
		const modifiers = match[1] ?? "";
		if (/\b(?:skip|todo|skipIf|runIf)\b/.test(modifiers)) {
			continue;
		}
		let start = match.index + match[0].length;
		// `it.each([...])(…)` and ``it.each`table`(…)`` put the table first and the test arguments in the NEXT group.
		if (/\b(?:each|for)\b/.test(modifiers)) {
			const table = closingIndex(masked, start);
			start = table === -1 ? -1 : nextGroup(masked, table + 1);
		}
		const end = start === -1 ? -1 : closingIndex(masked, start);
		if (end === -1) {
			continue;
		}
		if (assertionPattern.test(masked.slice(start, end))) {
			continue;
		}
		findings.push({
			line: source.slice(0, match.index).split("\n").length,
			name: source.slice(start, end).match(/["'`]([^"'`]*)/)?.[1]?.trim() ?? "<unnamed>",
		});
	}
	return findings;
}

function testFilesUnder(directory) {
	return readdirSync(directory, { recursive: true, withFileTypes: true })
		.filter((entry) => entry.isFile() && /\.test\.tsx?$/.test(entry.name))
		.map((entry) => join(entry.parentPath, entry.name))
		.sort();
}

export function checkTestsHaveAssertions(sourceRoot = resolve(frontendRoot, "src")) {
	const files = testFilesUnder(sourceRoot);
	const failures = files.flatMap((file) =>
		findTestsWithoutAssertions(readFileSync(file, "utf8")).map(
			({ line, name }) => `${relative(frontendRoot, file)}:${line}: ${name}`,
		),
	);
	if (failures.length > 0) {
		throw new Error(
			["Every test must assert something; these assert nothing:", ...failures.map((entry) => `  ${entry}`)].join("\n"),
		);
	}
	return files.length;
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	try {
		process.stdout.write(`Every test in all ${checkTestsHaveAssertions()} Vitest files asserts something.\n`);
	} catch (error) {
		process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
		process.exitCode = 1;
	}
}

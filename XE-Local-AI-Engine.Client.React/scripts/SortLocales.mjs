// Keeps src/locales/*.json in one canonical shape: object keys sorted byte-wise (UTF-8) at every depth, tab indent,
// trailing newline. Every feature branch appends keys, so append-order files conflicted on every absorb; sorted files
// put a new key next to its siblings and a conflict is resolved by re-running this script, never by hand.
//
//   node scripts/SortLocales.mjs           rewrite every locale file in place
//   node scripts/SortLocales.mjs --check   exit 1 naming the first unsorted file and key path; write nothing
//
// Values, arrays and nesting are kept as they are; only object key order changes.

import { readdirSync, readFileSync, writeFileSync } from "node:fs";
import { dirname, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const frontendRoot = resolve(dirname(fileURLToPath(import.meta.url)), "..");
const localesDirectory = join(frontendRoot, "src", "locales");

export const compareKeys = (left, right) => Buffer.compare(Buffer.from(left), Buffer.from(right));

const isObject = (value) => value !== null && typeof value === "object" && !Array.isArray(value);

// Serialized by hand rather than with JSON.stringify on a rebuilt object: JS objects iterate integer-like keys first
// whatever their insertion order, which would silently break the byte-wise order for a key such as "2" or "10".
export function serializeSorted(value, indent = "") {
	const inner = `${indent}\t`;
	if (Array.isArray(value)) {
		if (value.length === 0) {
			return "[]";
		}
		return `[\n${value.map((item) => inner + serializeSorted(item, inner)).join(",\n")}\n${indent}]`;
	}
	if (isObject(value)) {
		const keys = Object.keys(value).toSorted(compareKeys);
		if (keys.length === 0) {
			return "{}";
		}
		const entries = keys.map((key) => `${inner}${JSON.stringify(key)}: ${serializeSorted(value[key], inner)}`);
		return `{\n${entries.join(",\n")}\n${indent}}`;
	}
	return JSON.stringify(value);
}

// The first object (depth-first, file order) whose own keys are out of order, as "path.to.object: key"; null when
// the parsed tree is sorted. Only used to make the --check message point somewhere useful.
export function findFirstUnsortedKey(value, path = "") {
	if (Array.isArray(value)) {
		for (const [index, item] of value.entries()) {
			const found = findFirstUnsortedKey(item, `${path}[${index}]`);
			if (found) {
				return found;
			}
		}
		return null;
	}
	if (!isObject(value)) {
		return null;
	}
	const keys = Object.keys(value);
	for (let index = 1; index < keys.length; index += 1) {
		if (compareKeys(keys[index - 1], keys[index]) > 0) {
			return `${path || "<root>"}: "${keys[index]}" belongs before "${keys[index - 1]}"`;
		}
	}
	for (const key of keys) {
		const found = findFirstUnsortedKey(value[key], path ? `${path}.${key}` : key);
		if (found) {
			return found;
		}
	}
	return null;
}

export function sortLocaleText(text) {
	return `${serializeSorted(JSON.parse(text))}\n`;
}

export function sortLocales({ directory = localesDirectory, check = false } = {}) {
	const problems = [];
	for (const name of readdirSync(directory)
		.filter((file) => file.endsWith(".json"))
		.toSorted()) {
		const path = join(directory, name);
		const text = readFileSync(path, "utf8");
		const sorted = sortLocaleText(text);
		if (sorted === text) {
			continue;
		}
		if (check) {
			const where = findFirstUnsortedKey(JSON.parse(text)) ?? "formatting differs from the canonical shape";
			problems.push(`${relative(frontendRoot, path)}: ${where}`);
		} else {
			writeFileSync(path, sorted);
		}
	}
	return problems;
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	const check = process.argv.includes("--check");
	const problems = sortLocales({ check });
	if (problems.length > 0) {
		process.stderr.write(`Unsorted locale file. Run \`pnpm run locales:sort\`.\n  ${problems[0]}\n`);
		process.exitCode = 1;
	}
}

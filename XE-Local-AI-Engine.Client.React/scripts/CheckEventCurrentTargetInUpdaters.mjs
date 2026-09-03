/**
 * Static guard: fail CI when event.currentTarget or event.target is read INSIDE a deferred
 * functional state-updater callback (the React 19 null-ref footgun).
 *
 * Safe pattern (value captured before the updater):
 *   onChange={(event) => { const value = event.currentTarget.value; setValues((c) => ({ ...c, x: value })); }}
 *
 * Unsafe pattern (currentTarget read inside the updater — crashes under React 19 batching):
 *   onChange={(event) => setValues((c) => ({ ...c, x: event.currentTarget.value }))}
 *
 * Detection: for every functional-updater call site (set*(  (param) => … )), extract the full
 * updater body (balanced parens/braces) and check whether it reads .currentTarget or .target.
 * A value captured into a local const BEFORE the set*( call is safe by definition — it never
 * appears inside the updater body.
 *
 * Exits 0 when clean, 1 when violations found (prints file:line for each).
 */

import { readFileSync } from "node:fs";
import { join, relative } from "node:path";
import { readdirSync, statSync } from "node:fs";

const ROOT = join(import.meta.dirname, "..", "src");

// Matches the opening of a set*(  functional-updater call:
//   capture group 1: the set* callee name (e.g. setValues, setState, setTitle)
//   followed by the opening paren of the updater argument (not a direct value — must be followed
//   by an identifier + `) =>` or `=> (` or `=> {`)
// We match: setFoo(  (anyParam)  =>
const UPDATER_OPENER = /\b(set[A-Z][a-zA-Z]*)\s*\(\s*\([^)]*\)\s*=>/g;

// Inside an extracted updater body: reads of .currentTarget or .target that are NOT
// part of a local-const assignment (i.e. not `const ... = event.currentTarget`).
// We flag any occurrence of .currentTarget or .target inside the body.
const EVENT_PROP_IN_BODY = /\.(currentTarget|target)\b/;

/**
 * Walk directory recursively, yielding .ts and .tsx files.
 */
function* walkSrc(dir) {
	for (const entry of readdirSync(dir)) {
		const full = join(dir, entry);
		const stat = statSync(full);
		if (stat.isDirectory()) {
			yield* walkSrc(full);
		} else if (/\.(tsx?|ts)$/.test(entry) && !entry.endsWith(".d.ts")) {
			yield full;
		}
	}
}

/**
 * Given a source string and an offset pointing at the character AFTER the opening `(` of
 * the updater argument list (i.e. the `(` in `setValues(`), extract the full text of that
 * argument — balanced over `(`, `)`, `{`, `}`. Returns null if the source ends before the
 * closing paren is found.
 *
 * We start one level deep (we are inside the outer `set*(` paren already).
 */
function extractBalanced(src, startOffset) {
	let depth = 1;
	let i = startOffset;
	let inString = null; // null | '"' | "'" | '`'
	let escaped = false;

	while (i < src.length && depth > 0) {
		const ch = src[i];

		if (escaped) {
			escaped = false;
			i++;
			continue;
		}

		if (inString !== null) {
			if (ch === "\\" && inString !== "`") {
				escaped = true;
			} else if (ch === inString) {
				inString = null;
			}
			i++;
			continue;
		}

		if (ch === '"' || ch === "'" || ch === "`") {
			inString = ch;
			i++;
			continue;
		}

		if (ch === "(" || ch === "{") {
			depth++;
		} else if (ch === ")" || ch === "}") {
			depth--;
			if (depth === 0) {
				// Return everything up to (not including) the closing paren.
				return src.slice(startOffset, i);
			}
		}

		i++;
	}

	return null; // unterminated (shouldn't happen in valid TS)
}

/**
 * Given a file's source text, return an array of violations:
 * { line, callee, snippet }
 */
function findViolations(src) {
	const violations = [];
	const lines = src.split("\n");

	// Build a cumulative line-start-offset array for fast line-number lookup.
	const lineStarts = [0];
	for (let i = 0; i < lines.length; i++) {
		lineStarts.push(lineStarts[i] + lines[i].length + 1); // +1 for \n
	}

	function offsetToLine(offset) {
		let lo = 0;
		let hi = lineStarts.length - 1;
		while (lo < hi) {
			const mid = (lo + hi + 1) >> 1;
			if (lineStarts[mid] <= offset) {
				lo = mid;
			} else {
				hi = mid - 1;
			}
		}
		return lo + 1;
	}

	// Reset lastIndex before each file scan.
	UPDATER_OPENER.lastIndex = 0;

	// Avoid noAssignInExpressions: use exec in a for loop with an explicit re-exec.
	for (let match = UPDATER_OPENER.exec(src); match !== null; match = UPDATER_OPENER.exec(src)) {
		const callee = match[1];

		// UPDATER_OPENER matches `setFoo(  (param) =>` — the set*( open paren is
		// right after the callee name. Find it within the match string.
		const callOpenIdx = src.indexOf("(", match.index + callee.length);
		if (callOpenIdx === -1) {
			continue;
		}

		// Extract everything between the set*( opening paren and its matching close.
		const body = extractBalanced(src, callOpenIdx + 1);
		if (body === null) {
			continue;
		}

		// Check if the updater body contains .currentTarget or .target.
		if (EVENT_PROP_IN_BODY.test(body)) {
			const line = offsetToLine(match.index);
			// Build a terse snippet: first 120 chars of the body, collapsed whitespace.
			const snippet = body.replace(/\s+/g, " ").trim().slice(0, 120);
			violations.push({ line, callee, snippet });
		}
	}

	return violations;
}


let totalViolations = 0;
const allMessages = [];

for (const filePath of walkSrc(ROOT)) {
	const src = readFileSync(filePath, "utf8");
	const violations = findViolations(src);

	for (const v of violations) {
		const rel = relative(process.cwd(), filePath);
		const msg = `${rel}:${v.line}: event.currentTarget/.target read inside ${v.callee}() functional updater — capture to a const before the updater call`;
		allMessages.push(msg);
		totalViolations++;
	}
}

if (totalViolations === 0) {
	process.stdout.write("CheckEventCurrentTargetInUpdaters: OK (0 violations)\n");
	process.exit(0);
} else {
	process.stderr.write(`\nCheckEventCurrentTargetInUpdaters: ${totalViolations} violation(s) found:\n\n`);
	for (const msg of allMessages) {
		process.stderr.write(`  ${msg}\n`);
	}
	process.stderr.write(
		"\nFix: capture event.currentTarget.value into a local const BEFORE the state updater:\n" +
		"  onChange={(event) => { const value = event.currentTarget.value; setValues((c) => ({ ...c, field: value })); }}\n\n",
	);
	process.exit(1);
}

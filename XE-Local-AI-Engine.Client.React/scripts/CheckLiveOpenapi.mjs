import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { fetchNormalizedOpenapi, serializeOpenapi } from "./FetchOpenapi.mjs";

export function firstDifference(expected, actual) {
	const expectedLines = expected.split("\n");
	const actualLines = actual.split("\n");
	const lineCount = Math.max(expectedLines.length, actualLines.length);
	for (let index = 0; index < lineCount; index += 1) {
		if (expectedLines[index] !== actualLines[index]) {
			return {
				line: index + 1,
				expected: expectedLines[index] ?? "<end of file>",
				actual: actualLines[index] ?? "<end of file>",
			};
		}
	}
	return undefined;
}

export async function checkLiveOpenapi({
	specUrl = process.env.OPENAPI_SPEC_URL,
	committedPath = resolve(process.cwd(), "openapi/v1.json"),
} = {}) {
	if (!specUrl) {
		throw new Error("OPENAPI_SPEC_URL is required for the live OpenAPI drift check.");
	}

	const committed = readFileSync(committedPath, "utf8");
	const live = serializeOpenapi(await fetchNormalizedOpenapi(specUrl));
	const difference = firstDifference(committed, live);
	if (difference) {
		throw new Error(
			`Live OpenAPI differs from openapi/v1.json at line ${difference.line}.\n` +
				`committed: ${difference.expected}\n` +
				`live:      ${difference.actual}`,
		);
	}
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	checkLiveOpenapi()
		.then(() => process.stdout.write("Live OpenAPI matches openapi/v1.json; validating generated client snapshot next.\n"))
		.catch((error) => {
			process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
			process.exitCode = 1;
		});
}

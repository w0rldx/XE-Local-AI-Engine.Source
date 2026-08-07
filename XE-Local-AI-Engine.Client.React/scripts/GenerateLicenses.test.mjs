import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { dedupeAndSort, normalizeLicense } from "./GenerateLicenses.mjs";

test("normalizeLicense rejects missing license metadata", () => {
	assert.throws(() => normalizeLicense(""), /missing license metadata/i);
	assert.throws(() => normalizeLicense(undefined), /missing license metadata/i);
});

test("dedupeAndSort rejects unknown and NOASSERTION licenses", () => {
	for (const license of ["Unknown", "NOASSERTION"]) {
		assert.throws(
			() =>
				dedupeAndSort([
					{
						id: "frontend:example@1.0.0",
						name: "example",
						version: "1.0.0",
						license,
						source: "frontend",
					},
				]),
			/license.*not reviewable/i,
		);
	}
});

test("generator includes transitive NuGet packages and cannot reuse stale output", () => {
	const source = readFileSync(fileURLToPath(new URL("./GenerateLicenses.mjs", import.meta.url)), "utf8");
	assert.match(source, /"--include-transitive"/);
	assert.match(source, /"--exclude-publish-false"/);
	assert.match(source, /"--override-package-information"/);
	assert.match(source, /nuget-license-overrides\.json/);
	assert.doesNotMatch(source, /readPreviousBackendPackages/);
	assert.doesNotMatch(source, /directDependencyNames/);
});

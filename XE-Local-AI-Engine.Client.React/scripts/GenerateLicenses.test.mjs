import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";
import { fileURLToPath } from "node:url";

import {
	backendExcludedProjects,
	dedupeAndSort,
	isExcludedNativeBinary,
	normalizeLicense,
	RELEASE_RESTORE_ARGS,
} from "./GenerateLicenses.mjs";

// nuget-license 4.0.18 `StringExtensions.PathLike`: an entry (`*` and `?` wildcards, case-insensitive) matches the full
// project path or, failing that, its file name.
function nugetLicensePathLike(path, pattern) {
	const source = pattern
		.replace(/[.+^${}()|[\]\\]/g, "\\$&")
		.replaceAll("*", ".*")
		.replaceAll("?", ".");
	const regex = new RegExp(`^${source}$`, "is");
	return regex.test(path) || regex.test(path.split(/[\\/]/).at(-1));
}

test("the Release restore skips the NuGet audit so licenses:check works offline with cached packages", () => {
	assert.ok(RELEASE_RESTORE_ARGS.includes("-p:NuGetAudit=false"));
	assert.ok(RELEASE_RESTORE_ARGS.includes("-p:Configuration=Release"));
});

test("isExcludedNativeBinary matches rollup platform binaries on every OS but nothing shipped", () => {
	for (const platformName of ["@rollup/rollup-linux-x64-gnu", "@rollup/rollup-win32-x64-msvc", "@rollup/rollup-darwin-arm64"]) {
		assert.equal(isExcludedNativeBinary(platformName), true);
	}

	// Real shipped packages must never be excluded.
	assert.equal(isExcludedNativeBinary("react"), false);
	assert.equal(isExcludedNativeBinary("@rollup/plugin-node-resolve"), false);
});

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

test("backend project exclusions match the file name, not a checkout directory named tests", () => {
	const solution = `<Solution>
  <Project Path="XE-Local-AI-Engine.Client\\XE-Local-AI-Engine.Client.csproj"/>
  <Project Path="XE-Local-AI-Engine.Tests/XE-Local-AI-Engine.Tests.csproj"/>
  <Project Path="XE-Local-AI-Engine.Client.Testing/XE-Local-AI-Engine.Client.Testing.csproj"/>
  <Project Path="XE-Local-AI-Engine.AppHost/XE-Local-AI-Engine.AppHost.csproj"/>
</Solution>`;
	const entries = backendExcludedProjects(solution);
	const excluded = (path) => entries.some((entry) => nugetLicensePathLike(path, entry));
	const root = "/work/x-tests/checkout";

	assert.equal(excluded(`${root}/XE-Local-AI-Engine.Client/XE-Local-AI-Engine.Client.csproj`), false);
	for (const project of ["XE-Local-AI-Engine.Tests", "XE-Local-AI-Engine.Client.Testing", "XE-Local-AI-Engine.AppHost"]) {
		assert.equal(excluded(`${root}/${project}/${project}.csproj`), true, project);
	}
});

test("the real solution excludes its test projects and keeps the node", () => {
	const solution = readFileSync(fileURLToPath(new URL("../../XE-Local-AI-Engine.slnx", import.meta.url)), "utf8");
	const entries = backendExcludedProjects(solution);
	// Every project file the solution names, parsed independently of the generator's own pattern.
	const projects = [...solution.matchAll(/Path="([^"]+\.csproj)"/g)].map((match) => match[1].split(/[\\/]/).at(-1));
	const neverShipped = projects.filter((name) => /Tests|Testing|AppHost/i.test(name));

	assert.ok(neverShipped.includes("XE-Local-AI-Engine.Tests.csproj"));
	assert.ok(neverShipped.includes("XE-Local-AI-Engine.AppHost.csproj"));
	// Exactly the never-shipped set, so every other project (the node among them) stays in the scan.
	assert.deepEqual(entries.toSorted(), neverShipped.toSorted());
	assert.ok(projects.includes("XE-Local-AI-Engine.Client.csproj") && !entries.includes("XE-Local-AI-Engine.Client.csproj"));
});

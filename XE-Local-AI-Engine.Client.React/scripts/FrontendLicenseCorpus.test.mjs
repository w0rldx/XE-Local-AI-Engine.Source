import assert from "node:assert/strict";
import { mkdirSync, readFileSync, writeFileSync } from "node:fs";
import { createHash } from "node:crypto";
import { join } from "node:path";
import { test } from "node:test";
import { tmpdir } from "node:os";
import { mkdtempSync } from "node:fs";

import { buildFrontendLicenseCorpus } from "./FrontendLicenseCorpus.mjs";

function fixture(withLicense = true) {
	const root = mkdtempSync(join(tmpdir(), "xe-license-corpus-"));
	const packageRoot = join(root, "node_modules", "example");
	mkdirSync(packageRoot, { recursive: true });
	writeFileSync(join(packageRoot, "package.json"), JSON.stringify({ name: "example", version: "1.0.0" }));
	if (withLicense) {
		writeFileSync(join(packageRoot, "LICENSE"), "Copyright Example\nMIT terms\n");
	}
	return { root, packageRoot };
}

test("buildFrontendLicenseCorpus copies exact package terms for detected components", () => {
	const { root, packageRoot } = fixture();
	const output = join(root, "corpus");
	buildFrontendLicenseCorpus({
		componentManifest: {
			components: [{ name: "example", version: "1.0.0", license: "MIT", purl: "pkg:npm/example@1.0.0" }],
		},
		pnpmLicenseGroups: {
			MIT: [{ name: "example", versions: ["1.0.0"], paths: [packageRoot], license: "MIT", author: "Example Author" }],
		},
		outputDirectory: output,
	});
	assert.equal(readFileSync(join(output, "example@1.0.0", "LICENSE"), "utf8"), "Copyright Example\nMIT terms\n");
	const inventory = JSON.parse(readFileSync(join(output, "FRONTEND-COMPONENTS.json"), "utf8"));
	assert.equal(inventory.components.length, 1);
	assert.equal(inventory.components[0].purl, "pkg:npm/example@1.0.0");
	assert.deepEqual(inventory.components[0].licenseFiles, [
		{
			path: "example@1.0.0/LICENSE",
			sha256: createHash("sha256").update("Copyright Example\nMIT terms\n").digest("hex"),
		},
	]);
});

test("buildFrontendLicenseCorpus fails when a detected package has no exact license file", () => {
	const { root, packageRoot } = fixture(false);
	assert.throws(
		() =>
			buildFrontendLicenseCorpus({
				componentManifest: {
					components: [{ name: "example", version: "1.0.0", license: "MIT", purl: "pkg:npm/example@1.0.0" }],
				},
				pnpmLicenseGroups: {
					MIT: [{ name: "example", versions: ["1.0.0"], paths: [packageRoot], license: "MIT" }],
				},
				outputDirectory: join(root, "corpus"),
			}),
		/exact license file/i,
	);
});

test("buildFrontendLicenseCorpus uses a hash-pinned curated license when the package omits it", () => {
	const { root, packageRoot } = fixture(false);
	const curatedTerms = join(root, "third-party", "example-LICENSE.txt");
	mkdirSync(join(root, "third-party"), { recursive: true });
	writeFileSync(curatedTerms, "Copyright Example\nMIT terms\n");
	const output = join(root, "corpus");

	buildFrontendLicenseCorpus({
		componentManifest: {
			components: [{ name: "example", version: "1.0.0", license: "MIT", purl: "pkg:npm/example@1.0.0" }],
		},
		pnpmLicenseGroups: {
			MIT: [{ name: "example", versions: ["1.0.0"], paths: [packageRoot], license: "MIT" }],
		},
		curatedLicenseComponents: {
			"pkg:npm/example@1.0.0": {
				license: "MIT",
				basis: "The package metadata and pinned upstream license both declare MIT.",
				files: [
					{
						path: "third-party/example-LICENSE.txt",
						sha256: "7b38c6bf0ed500d681e249da31778c57aa0acddc49e336af52e777a4b0a13599",
						source: "https://example.test/v1.0.0/LICENSE",
					},
				],
			},
		},
		repositoryRoot: root,
		outputDirectory: output,
	});

	assert.equal(readFileSync(join(output, "example@1.0.0", "example-LICENSE.txt"), "utf8"), "Copyright Example\nMIT terms\n");
	const inventory = JSON.parse(readFileSync(join(output, "FRONTEND-COMPONENTS.json"), "utf8"));
	assert.equal(inventory.components[0].licenseEvidence, "curated-hash-pinned");
	assert.equal(inventory.components[0].licenseSources[0], "https://example.test/v1.0.0/LICENSE");
	assert.equal(inventory.components[0].licenseBasis, "The package metadata and pinned upstream license both declare MIT.");
});

test("buildFrontendLicenseCorpus rejects stale curated license entries", () => {
	const { root, packageRoot } = fixture();
	assert.throws(
		() =>
			buildFrontendLicenseCorpus({
				componentManifest: {
					components: [{ name: "example", version: "1.0.0", license: "MIT", purl: "pkg:npm/example@1.0.0" }],
				},
				pnpmLicenseGroups: {
					MIT: [{ name: "example", versions: ["1.0.0"], paths: [packageRoot], license: "MIT" }],
				},
				curatedLicenseComponents: {
					"pkg:npm/stale@1.0.0": { license: "MIT", basis: "test", files: [] },
				},
				repositoryRoot: root,
				outputDirectory: join(root, "corpus"),
			}),
		/stale curated frontend license entries/i,
	);
});

test("buildFrontendLicenseCorpus preserves nested vendored licenses and third-party notices", () => {
	const { root, packageRoot } = fixture(false);
	const nestedLicense = join(packageRoot, "lib-vendor", "dependency", "LICENSE");
	const nestedNotice = join(packageRoot, "src", "third-party-notices.txt");
	mkdirSync(join(packageRoot, "lib-vendor", "dependency"), { recursive: true });
	mkdirSync(join(packageRoot, "src"), { recursive: true });
	writeFileSync(nestedLicense, "ISC terms\n");
	writeFileSync(nestedNotice, "Vendored dependency notices\n");
	const curatedTerms = join(root, "third-party", "example-LICENSE.txt");
	mkdirSync(join(root, "third-party"), { recursive: true });
	writeFileSync(curatedTerms, "MIT terms\n");
	const output = join(root, "corpus");

	buildFrontendLicenseCorpus({
		componentManifest: {
			components: [{ name: "example", version: "1.0.0", license: "MIT AND ISC", purl: "pkg:npm/example@1.0.0" }],
		},
		pnpmLicenseGroups: {
			"MIT AND ISC": [
				{ name: "example", versions: ["1.0.0"], paths: [packageRoot], license: "MIT AND ISC" },
			],
		},
		curatedLicenseComponents: {
			"pkg:npm/example@1.0.0": {
				license: "MIT AND ISC",
				basis: "The package terms supplement the exact vendored dependency terms.",
				files: [
					{
						path: "third-party/example-LICENSE.txt",
						sha256: "d0febe6f2e066329c487c2621cd02a4eddce97b51cdacb22600dd5181d843c08",
						source: "https://example.test/LICENSE",
					},
				],
			},
		},
		repositoryRoot: root,
		outputDirectory: output,
	});

	assert.equal(readFileSync(join(output, "example@1.0.0", "lib-vendor", "dependency", "LICENSE"), "utf8"), "ISC terms\n");
	assert.equal(
		readFileSync(join(output, "example@1.0.0", "src", "third-party-notices.txt"), "utf8"),
		"Vendored dependency notices\n",
	);
	assert.equal(readFileSync(join(output, "example@1.0.0", "example-LICENSE.txt"), "utf8"), "MIT terms\n");
	const inventory = JSON.parse(readFileSync(join(output, "FRONTEND-COMPONENTS.json"), "utf8"));
	assert.equal(inventory.components[0].licenseEvidence, "installed-package+curated-hash-pinned");
	assert.deepEqual(
		inventory.components[0].licenseFiles.map((file) => file.path),
		[
			"example@1.0.0/lib-vendor/dependency/LICENSE",
			"example@1.0.0/src/third-party-notices.txt",
			"example@1.0.0/example-LICENSE.txt",
		],
	);
});

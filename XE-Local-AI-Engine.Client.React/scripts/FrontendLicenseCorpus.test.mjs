import { buildFrontendLicenseCorpus } from "./FrontendLicenseCorpus.mjs";
import assert from "node:assert/strict";
import { createHash } from "node:crypto";
import { mkdirSync, mkdtempSync, readFileSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import { test } from "node:test";

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

test("buildFrontendLicenseCorpus identifies a curated override migration when a package without terms is upgraded", () => {
	const { root, packageRoot } = fixture(false);
	writeFileSync(join(packageRoot, "package.json"), JSON.stringify({ name: "example", version: "2.0.0" }));
	assert.throws(
		() =>
			buildFrontendLicenseCorpus({
				componentManifest: {
					components: [{ name: "example", version: "2.0.0", license: "MIT", purl: "pkg:npm/example@2.0.0" }],
				},
				pnpmLicenseGroups: {
					MIT: [{ name: "example", versions: ["2.0.0"], paths: [packageRoot], license: "MIT" }],
				},
				curatedLicenseComponents: {
					"pkg:npm/example@1.0.0": {
						license: "MIT",
						basis: "Pinned evidence for the old version.",
						files: [
							{
								path: "third-party/npm/example/1.0.0/LICENSE.txt",
								sha256: "a".repeat(64),
								source: "https://example.test/v1.0.0/LICENSE",
							},
						],
					},
				},
				repositoryRoot: root,
				outputDirectory: join(root, "corpus"),
			}),
		(error) => {
			assert.match(error.message, /no exact license file/i);
			assert.match(error.message, /installed version 2\.0\.0 \(pkg:npm\/example@2\.0\.0\)/i);
			assert.match(error.message, /1\.0\.0 \(pkg:npm\/example@1\.0\.0\)/i);
			assert.match(error.message, /third-party\/npm\/example\/1\.0\.0\/LICENSE\.txt/i);
			assert.match(error.message, /https:\/\/example\.test\/v1\.0\.0\/LICENSE/i);
			assert.match(error.message, /a{64}/iu);
			assert.match(error.message, /installed package terms: absent/i);
			assert.match(error.message, /old evidence applicability: unverified; human comparison required/i);
			assert.match(error.message, /third-party\/npm\/frontend-license-overrides\.json/i);
			assert.match(error.message, /source, tag, and SHA-256/i);
			return true;
		},
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

test("buildFrontendLicenseCorpus still refuses missing or changed curated evidence", () => {
	const { root, packageRoot } = fixture(false);
	const curatedTerms = join(root, "third-party", "example-LICENSE.txt");
	mkdirSync(join(root, "third-party"), { recursive: true });
	writeFileSync(curatedTerms, "changed terms\n");
	const curatedEntry = {
		license: "MIT",
		basis: "Pinned exact evidence.",
		files: [
			{
				path: "third-party/example-LICENSE.txt",
				sha256: "0".repeat(64),
				source: "https://example.test/v1.0.0/LICENSE",
			},
		],
	};
	const options = {
		componentManifest: {
			components: [{ name: "example", version: "1.0.0", license: "MIT", purl: "pkg:npm/example@1.0.0" }],
		},
		pnpmLicenseGroups: {
			MIT: [{ name: "example", versions: ["1.0.0"], paths: [packageRoot], license: "MIT" }],
		},
		curatedLicenseComponents: { "pkg:npm/example@1.0.0": curatedEntry },
		repositoryRoot: root,
		outputDirectory: join(root, "corpus"),
	};

	assert.throws(() => buildFrontendLicenseCorpus(options), /hash mismatch/i);
	curatedEntry.files[0].path = "third-party/missing-LICENSE.txt";
	assert.throws(() => buildFrontendLicenseCorpus(options), /file is missing/i);
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

test("buildFrontendLicenseCorpus identifies a stale override that likely belongs to an installed newer version", () => {
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
					"pkg:npm/example@0.9.0": {
						license: "MIT",
						basis: "Old exact evidence.",
						files: [
							{
								path: "third-party/npm/example/0.9.0/LICENSE.txt",
								sha256: "b".repeat(64),
								source: "https://example.test/v0.9.0/LICENSE",
							},
						],
					},
				},
				repositoryRoot: root,
				outputDirectory: join(root, "corpus"),
			}),
		(error) => {
			assert.match(error.message, /stale curated frontend license entries/i);
			assert.match(error.message, /0\.9\.0 \(pkg:npm\/example@0\.9\.0\) -> 1\.0\.0 \(pkg:npm\/example@1\.0\.0\)/i);
			assert.match(error.message, /third-party\/npm\/example\/0\.9\.0\/LICENSE\.txt/i);
			assert.match(error.message, /https:\/\/example\.test\/v0\.9\.0\/LICENSE/i);
			assert.match(error.message, /b{64}/iu);
			assert.match(error.message, /installed package terms: present/i);
			assert.match(error.message, /old evidence applicability: unverified; human comparison required/i);
			assert.match(error.message, /third-party\/npm\/frontend-license-overrides\.json/i);
			assert.match(error.message, /do not retarget license evidence, sources, tags, or hashes/i);
			return true;
		},
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
			"MIT AND ISC": [{ name: "example", versions: ["1.0.0"], paths: [packageRoot], license: "MIT AND ISC" }],
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

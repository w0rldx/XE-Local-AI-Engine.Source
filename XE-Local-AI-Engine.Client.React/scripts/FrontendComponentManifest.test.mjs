import assert from "node:assert/strict";
import { mkdirSync, writeFileSync } from "node:fs";
import { join } from "node:path";
import { test } from "node:test";
import { tmpdir } from "node:os";
import { mkdtempSync } from "node:fs";

import { createFrontendComponentManifestPlugin, resolvePackageComponent } from "./FrontendComponentManifest.mjs";

test("resolvePackageComponent reads exact installed package identity", () => {
	const root = mkdtempSync(join(tmpdir(), "xe-component-"));
	const packageRoot = join(root, "node_modules", "example-package");
	mkdirSync(join(packageRoot, "dist"), { recursive: true });
	writeFileSync(
		join(packageRoot, "package.json"),
		JSON.stringify({ name: "example-package", version: "1.2.3", license: "MIT" }),
	);
	const component = resolvePackageComponent(join(packageRoot, "dist", "index.js"));
	assert.deepEqual(component, {
		name: "example-package",
		version: "1.2.3",
		license: "MIT",
		purl: "pkg:npm/example-package@1.2.3",
	});
});

test("resolvePackageComponent skips nested package metadata without an identity", () => {
	const root = mkdtempSync(join(tmpdir(), "xe-component-"));
	const packageRoot = join(root, "node_modules", "example-package");
	mkdirSync(join(packageRoot, "dist", "locales"), { recursive: true });
	writeFileSync(
		join(packageRoot, "package.json"),
		JSON.stringify({ name: "example-package", version: "1.2.3", license: "MIT" }),
	);
	writeFileSync(join(packageRoot, "dist", "locales", "package.json"), JSON.stringify({ type: "module" }));

	const component = resolvePackageComponent(join(packageRoot, "dist", "locales", "en.js"));
	assert.deepEqual(component, {
		name: "example-package",
		version: "1.2.3",
		license: "MIT",
		purl: "pkg:npm/example-package@1.2.3",
	});
});

test("plugin emits only rendered chunk component identities without local paths", () => {
	const root = mkdtempSync(join(tmpdir(), "xe-component-"));
	const packageRoot = join(root, "node_modules", "@scope", "example");
	const treeShakenPackageRoot = join(root, "node_modules", "tree-shaken-package");
	mkdirSync(join(packageRoot, "dist"), { recursive: true });
	mkdirSync(join(treeShakenPackageRoot, "dist"), { recursive: true });
	writeFileSync(join(packageRoot, "package.json"), JSON.stringify({ name: "@scope/example", version: "2.0.0", license: "Apache-2.0" }));
	writeFileSync(
		join(treeShakenPackageRoot, "package.json"),
		JSON.stringify({ name: "tree-shaken-package", version: "1.0.0", license: "MIT" }),
	);
	const renderedModule = join(packageRoot, "dist", "a.js");
	const treeShakenModule = join(treeShakenPackageRoot, "dist", "unused.js");
	const localModule = join(root, "src", "App.tsx");
	const plugin = createFrontendComponentManifestPlugin();
	let emitted;
	plugin.generateBundle.call(
		{
			getModuleIds: () => [renderedModule, treeShakenModule, localModule],
			emitFile: (asset) => {
				emitted = asset;
			},
		},
		{},
		{
			"assets/index.js": {
				type: "chunk",
				modules: {
					[renderedModule]: {},
					[localModule]: {},
				},
			},
		},
	);
	assert.equal(emitted.fileName, "component-manifest.json");
	const payload = JSON.parse(emitted.source);
	assert.equal(payload.components.length, 1);
	assert.equal(payload.components[0].purl, "pkg:npm/%40scope/example@2.0.0");
	assert.doesNotMatch(emitted.source, new RegExp(root.replaceAll("\\", "\\\\")));
});

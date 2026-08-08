import { existsSync, readFileSync } from "node:fs";
import { dirname, parse, resolve } from "node:path";

function normalizeModulePath(moduleId) {
	const withoutNullPrefix = moduleId.replace(/^\0/, "");
	const withoutQuery = withoutNullPrefix.split("?", 1)[0];
	return withoutQuery.startsWith("file://") ? new URL(withoutQuery) : resolve(withoutQuery.replace(/^\/@fs\//, "/"));
}

function createNpmPurl(name, version) {
	const encodedName = name.split("/").map((segment) => encodeURIComponent(segment)).join("/");
	return `pkg:npm/${encodedName}@${version}`;
}

/**
 * @param {string} moduleId
 * @returns {{ name: string; version: string; license: string; purl: string } | null}
 */
export function resolvePackageComponent(moduleId) {
	if (typeof moduleId !== "string" || !moduleId.includes("node_modules")) {
		return null;
	}

	let current = normalizeModulePath(moduleId);
	if (current instanceof URL) {
		current = current.pathname;
	}
	current = dirname(current);
	const root = parse(current).root;
	let incompleteMetadataPath = null;
	while (current !== root) {
		const packageJsonPath = resolve(current, "package.json");
		if (existsSync(packageJsonPath)) {
			const metadata = JSON.parse(readFileSync(packageJsonPath, "utf8"));
			if (
				typeof metadata.name !== "string" ||
				typeof metadata.version !== "string" ||
				typeof metadata.license !== "string" ||
				metadata.license.trim() === ""
			) {
				incompleteMetadataPath ??= packageJsonPath;
				current = dirname(current);
				continue;
			}

			return {
				name: metadata.name,
				version: metadata.version,
				license: metadata.license,
				purl: createNpmPurl(metadata.name, metadata.version),
			};
		}
		current = dirname(current);
	}

	if (incompleteMetadataPath) {
		throw new Error(`Bundled package metadata is incomplete: ${incompleteMetadataPath}`);
	}
	throw new Error(`Could not resolve package metadata for bundled module: ${moduleId}`);
}

export function createFrontendComponentManifestPlugin() {
	return {
		name: "xe-frontend-component-manifest",
		apply: "build",
		generateBundle(_outputOptions, bundle) {
			const byPurl = new Map();
			for (const output of Object.values(bundle)) {
				if (output.type !== "chunk") {
					continue;
				}
				for (const moduleId of Object.keys(output.modules)) {
					const component = resolvePackageComponent(moduleId);
					if (component) {
						byPurl.set(component.purl, component);
					}
				}
			}

			const payload = {
				schemaVersion: 1,
				generatedBy: "vite-rollup-rendered-chunks",
				components: [...byPurl.values()].sort((left, right) => left.purl.localeCompare(right.purl, "en")),
			};
			this.emitFile({
				type: "asset",
				fileName: "component-manifest.json",
				source: `${JSON.stringify(payload, null, 2)}\n`,
			});
		},
	};
}

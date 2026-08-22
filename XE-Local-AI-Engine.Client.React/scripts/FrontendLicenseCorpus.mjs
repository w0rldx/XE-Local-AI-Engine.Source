import { spawnSync } from "node:child_process";
import { createHash } from "node:crypto";
import { copyFileSync, existsSync, mkdirSync, readdirSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { basename, dirname, isAbsolute, join, relative, resolve } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const reactRoot = resolve(scriptDirectory, "..");
const repositoryRoot = resolve(reactRoot, "..");
const curatedManifestRepositoryPath = "third-party/npm/frontend-license-overrides.json";

function licenseFiles(packageRoot) {
	const candidates = [];
	function visit(directory) {
		for (const entry of readdirSync(directory, { withFileTypes: true })) {
			const path = join(directory, entry.name);
			const lower = entry.name.toLowerCase();
			if (entry.isDirectory() && lower !== "node_modules") {
				visit(path);
			} else if (
				entry.isFile() &&
				["licen", "copying", "notice", "copyright", "third-party", "third_party", "thirdparty"].some((prefix) =>
					lower.startsWith(prefix),
				)
			) {
				candidates.push(path);
			}
		}
	}
	visit(packageRoot);
	return candidates.sort((left, right) => left.localeCompare(right, "en"));
}

function installedPackageRoot(entry, version) {
	for (const path of entry.paths ?? []) {
		const packageJsonPath = join(path, "package.json");
		if (!existsSync(packageJsonPath)) {
			continue;
		}
		const metadata = JSON.parse(readFileSync(packageJsonPath, "utf8"));
		if (metadata.version === version) {
			return path;
		}
	}
	return undefined;
}

function npmPurlIdentity(purl) {
	const prefix = "pkg:npm/";
	if (typeof purl !== "string" || !purl.startsWith(prefix)) {
		return undefined;
	}
	const versionSeparator = purl.lastIndexOf("@");
	if (versionSeparator < prefix.length) {
		return undefined;
	}
	try {
		return {
			name: decodeURIComponent(purl.slice(prefix.length, versionSeparator)),
			version: decodeURIComponent(purl.slice(versionSeparator + 1)),
		};
	} catch {
		return undefined;
	}
}

function curatedEvidence(files) {
	if (!Array.isArray(files)) {
		return [];
	}
	return files.map((file) => ({
		path: typeof file?.path === "string" ? file.path : "<missing curated evidence path>",
		source: typeof file?.source === "string" ? file.source : "<missing curated source>",
		sha256: typeof file?.sha256 === "string" ? file.sha256 : "<missing curated SHA-256>",
	}));
}

function describeCuratedEvidence(evidence) {
	if (evidence.length === 0) {
		return "<missing curated evidence metadata>";
	}
	return evidence.map(({ path, source, sha256 }) => `${path} (source: ${source}; SHA-256: ${sha256})`).join(", ");
}

function curatedVersionMigrations(component, curatedLicenseComponents) {
	return Object.entries(curatedLicenseComponents).flatMap(([purl, curated]) => {
		const identity = npmPurlIdentity(purl);
		if (!identity || identity.name !== component.name || purl === component.purl) {
			return [];
		}
		return [{ purl, version: identity.version, evidence: curatedEvidence(curated?.files) }];
	});
}

function migrationReviewMessage(component, migrations) {
	const candidates = migrations.map(
		({ purl, version, evidence }) => `${version} (${purl}); prior evidence: ${describeCuratedEvidence(evidence)}`,
	);
	return [
		`Installed version ${component.version} (${component.purl}) has curated override candidates for another version: ${candidates.join("; ")}.`,
		"Installed package terms: absent. Old evidence applicability: unverified; human comparison required.",
		`Human review required in ${curatedManifestRepositoryPath}: verify or replace the listed license evidence, source, tag, and SHA-256 before adding an exact override for ${component.purl}.`,
	].join(" ");
}

function curatedLicenseFiles(component, curatedLicenseComponents, sourceRoot) {
	const curated = curatedLicenseComponents[component.purl];
	if (!curated) {
		return undefined;
	}
	if (
		curated.license !== component.license ||
		typeof curated.basis !== "string" ||
		curated.basis.trim() === "" ||
		!Array.isArray(curated.files) ||
		curated.files.length === 0
	) {
		throw new Error(`Curated frontend license entry is incomplete or disagrees for ${component.purl}`);
	}

	return curated.files.map((entry) => {
		if (
			typeof entry.path !== "string" ||
			typeof entry.sha256 !== "string" ||
			!/^[a-f0-9]{64}$/u.test(entry.sha256) ||
			typeof entry.source !== "string" ||
			entry.source.trim() === ""
		) {
			throw new Error(`Curated frontend license file metadata is incomplete for ${component.purl}`);
		}
		const sourcePath = resolve(sourceRoot, entry.path);
		const relativePath = relative(sourceRoot, sourcePath);
		if (relativePath.startsWith("..") || isAbsolute(relativePath) || !existsSync(sourcePath)) {
			throw new Error(`Curated frontend license file is missing or escapes the repository: ${entry.path}`);
		}
		const actualHash = createHash("sha256").update(readFileSync(sourcePath)).digest("hex");
		if (actualHash !== entry.sha256) {
			throw new Error(`Curated frontend license hash mismatch for ${component.purl}: ${entry.path}`);
		}
		return { path: sourcePath, outputPath: basename(sourcePath), source: entry.source, basis: curated.basis };
	});
}

export function buildFrontendLicenseCorpus({
	componentManifest,
	pnpmLicenseGroups,
	curatedLicenseComponents = {},
	repositoryRoot: sourceRoot = repositoryRoot,
	outputDirectory,
}) {
	if (!Array.isArray(componentManifest.components) || componentManifest.components.length === 0) {
		throw new Error("Frontend component manifest contains no detected components");
	}

	const licenseEntries = Object.values(pnpmLicenseGroups).flat();
	rmSync(outputDirectory, { recursive: true, force: true });
	mkdirSync(outputDirectory, { recursive: true });
	const inventory = [];
	const usedCuratedEntries = new Set();
	const installedPackageTerms = new Map();

	for (const component of componentManifest.components) {
		const entry = licenseEntries.find(
			(candidate) => candidate.name === component.name && (candidate.versions ?? []).includes(component.version),
		);
		if (!entry) {
			throw new Error(`Bundled component is absent from pnpm's production license inventory: ${component.purl}`);
		}
		if (entry.license !== component.license) {
			throw new Error(`License metadata disagrees for ${component.purl}: ${component.license} vs ${entry.license}`);
		}

		const packageRoot = installedPackageRoot(entry, component.version);
		if (!packageRoot) {
			throw new Error(`Could not resolve installed package path for ${component.purl}`);
		}
		const packageFiles = licenseFiles(packageRoot).map((path) => ({
			path,
			outputPath: relative(packageRoot, path),
			source: undefined,
			basis: undefined,
		}));
		installedPackageTerms.set(component.purl, packageFiles.length > 0);
		const curatedFiles = curatedLicenseFiles(component, curatedLicenseComponents, sourceRoot);
		if (packageFiles.length === 0 && !curatedFiles) {
			const migrations = curatedVersionMigrations(component, curatedLicenseComponents);
			const migrationDetails = migrations.length > 0 ? ` ${migrationReviewMessage(component, migrations)}` : "";
			throw new Error(
				`Bundled component has no exact license file in its installed package: ${component.purl}.${migrationDetails}`,
			);
		}
		if (curatedFiles) {
			usedCuratedEntries.add(component.purl);
		}
		const files = [...packageFiles, ...(curatedFiles ?? [])];

		const componentDirectoryName = `${encodeURIComponent(component.name)}@${component.version}`;
		const componentDirectory = join(outputDirectory, componentDirectoryName);
		mkdirSync(componentDirectory, { recursive: true });
		const copiedFiles = [];
		const copiedDestinations = new Set();
		for (const file of files) {
			const outputPath = file.outputPath.replaceAll("\\", "/");
			if (outputPath.startsWith("../") || isAbsolute(outputPath) || copiedDestinations.has(outputPath)) {
				throw new Error(`Invalid or duplicate frontend license destination for ${component.purl}: ${outputPath}`);
			}
			copiedDestinations.add(outputPath);
			const destination = join(componentDirectory, outputPath);
			mkdirSync(dirname(destination), { recursive: true });
			copyFileSync(file.path, destination);
			copiedFiles.push({
				path: `${componentDirectoryName}/${outputPath}`,
				sha256: createHash("sha256").update(readFileSync(destination)).digest("hex"),
			});
		}

		inventory.push({
			...component,
			author: typeof entry.author === "string" ? entry.author : undefined,
			homepage: typeof entry.homepage === "string" ? entry.homepage : undefined,
			licenseFiles: copiedFiles,
			licenseEvidence: curatedFiles
				? packageFiles.length > 0
					? "installed-package+curated-hash-pinned"
					: "curated-hash-pinned"
				: "installed-package",
			licenseSources: curatedFiles?.map((file) => file.source),
			licenseBasis: curatedFiles?.[0].basis,
		});
	}

	const staleCuratedEntries = Object.keys(curatedLicenseComponents).filter((purl) => !usedCuratedEntries.has(purl));
	if (staleCuratedEntries.length > 0) {
		const bundledComponents = componentManifest.components;
		const migrations = staleCuratedEntries.flatMap((purl) => {
			const staleIdentity = npmPurlIdentity(purl);
			if (!staleIdentity) {
				return [];
			}
			const installed = bundledComponents.find((component) => component.name === staleIdentity.name && component.purl !== purl);
			if (!installed) {
				return [];
			}
			return [
				{
					oldPurl: purl,
					oldVersion: staleIdentity.version,
					installed,
					evidence: curatedEvidence(curatedLicenseComponents[purl]?.files),
					installedTermsPresent: installedPackageTerms.get(installed.purl) === true,
				},
			];
		});
		const migrationDescriptions = migrations.map(
			({ oldPurl, oldVersion, installed, evidence, installedTermsPresent }) =>
				`${oldVersion} (${oldPurl}) -> ${installed.version} (${installed.purl}); prior evidence: ${describeCuratedEvidence(evidence)}; installed package terms: ${installedTermsPresent ? "present" : "absent"}; old evidence applicability: unverified; human comparison required`,
		);
		const migrationDetails =
			migrations.length > 0
				? ` Likely version migrations: ${migrationDescriptions.join("; ")}. Human review required in ${curatedManifestRepositoryPath}; do not retarget license evidence, sources, tags, or hashes without verification.`
				: "";
		throw new Error(`Stale curated frontend license entries: ${staleCuratedEntries.join(", ")}.${migrationDetails}`);
	}

	inventory.sort((left, right) => left.purl.localeCompare(right.purl, "en"));
	writeFileSync(
		join(outputDirectory, "FRONTEND-COMPONENTS.json"),
		`${JSON.stringify({ schemaVersion: 2, detectionSource: "dist/component-manifest.json", components: inventory }, null, 2)}\n`,
	);
	const notices = inventory.flatMap((component) => [
		`## ${component.name} ${component.version}`,
		"",
		`- License: ${component.license}`,
		`- Package URL: ${component.purl}`,
		...(component.author ? [`- Author: ${component.author}`] : []),
		...(component.homepage ? [`- Homepage: ${component.homepage}`] : []),
		`- Exact terms: ${component.licenseFiles.map((file) => file.path).join(", ")}`,
		"",
	]);
	writeFileSync(
		join(outputDirectory, "THIRD-PARTY-NOTICES.md"),
		["# Bundled frontend third-party notices", "", ...notices].join("\n"),
	);
	return inventory;
}

function argument(name, fallback) {
	const index = process.argv.indexOf(name);
	return index >= 0 && process.argv[index + 1] ? resolve(process.argv[index + 1]) : fallback;
}

function main() {
	const manifestPath = argument("--manifest", resolve(reactRoot, "dist/component-manifest.json"));
	const outputDirectory = argument("--output", resolve(reactRoot, "dist/licenses/frontend"));
	const curatedManifestPath = argument(
		"--curated-manifest",
		resolve(repositoryRoot, "third-party/npm/frontend-license-overrides.json"),
	);
	const result = spawnSync("pnpm", ["licenses", "list", "--prod", "--json"], {
		cwd: reactRoot,
		encoding: "utf8",
		maxBuffer: 64 * 1024 * 1024,
		shell: process.platform === "win32",
	});
	if (result.status !== 0 || typeof result.stdout !== "string" || result.stdout.trim() === "") {
		throw new Error(`pnpm licenses list failed: ${result.stderr || result.error || "unknown error"}`);
	}
	const inventory = buildFrontendLicenseCorpus({
		componentManifest: JSON.parse(readFileSync(manifestPath, "utf8")),
		pnpmLicenseGroups: JSON.parse(result.stdout),
		curatedLicenseComponents: JSON.parse(readFileSync(curatedManifestPath, "utf8")).components,
		repositoryRoot,
		outputDirectory,
	});
	process.stdout.write(`wrote exact license corpus for ${inventory.length} bundled frontend components\n`);
}

if (process.argv[1] && resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
	main();
}

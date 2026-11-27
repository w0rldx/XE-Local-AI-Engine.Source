// Fails the build on HubConnectionBuilder outside src/core/api/signalr/BuildHubConnection.ts. Biome's
// noRestrictedImports catches the named import, but `import * as signalR` and the default import reach the same
// class as `signalR.HubConnectionBuilder` unchecked. Test files are exempt: they mock the builder or drive the real
// library on purpose. Any mention counts, comments included, so a product file names buildHubConnection instead.

import { readdirSync, readFileSync } from "node:fs";
import { dirname, join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const frontendRoot = resolve(scriptDirectory, "..");
const builderFile = "src/core/api/signalr/BuildHubConnection.ts";

/** Product source files under `sourceRoot` (relative to `root`, `/`-separated) that name HubConnectionBuilder. */
export function findHubConnectionBuilderUses(root = frontendRoot, sourceRoot = resolve(root, "src")) {
	return readdirSync(sourceRoot, { recursive: true, withFileTypes: true })
		.filter((entry) => entry.isFile() && /\.tsx?$/.test(entry.name) && !/\.test\.tsx?$/.test(entry.name))
		.map((entry) => relative(root, join(entry.parentPath, entry.name)).split(sep).join("/"))
		.filter((file) => file !== builderFile && /\bHubConnectionBuilder\b/.test(readFileSync(resolve(root, file), "utf8")))
		.sort();
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	const uses = findHubConnectionBuilderUses();
	if (uses.length > 0) {
		process.stderr.write(
			`Build hub connections with buildHubConnection (${builderFile}); these files name HubConnectionBuilder:\n` +
				`${uses.map((file) => `  ${file}`).join("\n")}\n`,
		);
		process.exitCode = 1;
	} else {
		process.stdout.write(`HubConnectionBuilder appears only in ${builderFile}.\n`);
	}
}

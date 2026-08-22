import { spawnSync } from "node:child_process";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

const stages = [
	{ name: "frozen dependency install", command: "pnpm", args: ["install", "--frozen-lockfile"] },
	{ name: "OpenAPI drift check", command: "pnpm", args: ["run", "openapi:check"] },
	{ name: "About-dialog license refresh", command: "pnpm", args: ["run", "licenses:check"] },
	{ name: "frontend validation", command: "pnpm", args: ["run", "validate"] },
	{ name: "production build and license corpus", command: "pnpm", args: ["run", "build"] },
];

const derivedPaths = ["openapi", "src/core/api/generated", "src/features/about/data/third-party-licenses.generated.json"];

function describeFailure(result) {
	if (result.error) {
		return result.error.message;
	}
	if (result.status === null) {
		return `terminated${result.signal ? ` by ${result.signal}` : " without an exit status"}`;
	}
	return `exit ${result.status}`;
}

export function refreshDependencies({
	cwd = process.cwd(),
	platform = process.platform,
	spawn = spawnSync,
	stdout = process.stdout,
	stderr = process.stderr,
} = {}) {
	const failures = [];

	function runStage(stage) {
		stdout.write(`\n=== ${stage.name} ===\n`);
		const result = spawn(stage.command, stage.args, {
			cwd,
			shell: platform === "win32",
			stdio: "inherit",
		});
		if (result.error || result.status !== 0) {
			const reason = describeFailure(result);
			failures.push(`${stage.name}: ${reason}`);
			stderr.write(`${stage.name} failed (${reason}); continuing to collect dependency-refresh diagnostics.\n`);
			return false;
		}
		return true;
	}

	const [installStage, ...dependentStages] = stages;
	if (!runStage(installStage)) {
		for (const stage of dependentStages) {
			stderr.write(`${stage.name} skipped because the frozen dependency install failed.\n`);
		}
		stderr.write(`\nDependency refresh failed in ${failures.length} required step(s):\n`);
		for (const failure of failures) {
			stderr.write(`  - ${failure}\n`);
		}
		return 1;
	}

	for (const stage of dependentStages) {
		runStage(stage);
	}

	stdout.write("\n=== tracked derived files ===\n");
	const status = spawn("git", ["status", "--short", "--untracked-files=no", "--", ...derivedPaths], {
		cwd,
		encoding: "utf8",
		shell: false,
	});
	if (status.error || status.status !== 0) {
		const reason = describeFailure(status);
		failures.push(`tracked derived file report: ${reason}`);
		stderr.write(`Could not report tracked derived files (${reason}).\n`);
	} else {
		const changedFiles = status.stdout
			.split(/\r?\n/u)
			.filter((line) => line.length >= 4)
			.map((line) => line.slice(3).trim());
		if (changedFiles.length === 0) {
			stdout.write("No tracked derived files require committing.\n");
		} else {
			stdout.write("Commit these regenerated tracked files with the dependency update:\n");
			for (const path of changedFiles) {
				stdout.write(`  ${path}\n`);
			}
		}
	}

	if (failures.length === 0) {
		stdout.write("\nDependency refresh completed successfully.\n");
		return 0;
	}

	stderr.write(`\nDependency refresh failed in ${failures.length} required step(s):\n`);
	for (const failure of failures) {
		stderr.write(`  - ${failure}\n`);
	}
	return 1;
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	try {
		process.exitCode = refreshDependencies();
	} catch (error) {
		process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
		process.exitCode = 1;
	}
}

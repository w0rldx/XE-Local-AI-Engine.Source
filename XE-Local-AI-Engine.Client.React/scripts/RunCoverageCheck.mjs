import { spawnSync } from "node:child_process";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

/**
 * Runs the vitest coverage suite with threshold enforcement switched on.
 *
 * `vite.config.ts` builds `coverageThresholds` only when VITEST_COVERAGE_CHECK is "true", so the
 * gate is the variable, not the vitest flags. The obvious spelling — an inline `VAR=value` prefix in
 * the package script — is POSIX-only: pnpm runs package scripts through cmd on Windows, which parses
 * the prefix as a command name and dies with "'VITEST_COVERAGE_CHECK' is not recognized as an
 * internal or external command". That made publish/package-tester-win.ps1 unable to clear its own
 * coverage gate. Setting the variable on the child environment keeps one identical gate everywhere.
 */
export function runCoverageCheck({ env = process.env, platform = process.platform, spawn = spawnSync } = {}) {
	// pnpm is a .cmd shim on Windows, so Node must invoke it through the shell there — the same
	// rationale as RunPackageTool.mjs. That helper is not reused: it captures output for parsing,
	// and a test run has to stream straight to the console.
	const result = spawn("pnpm", ["exec", "vitest", "run", "--coverage"], {
		env: { ...env, VITEST_COVERAGE_CHECK: "true" },
		shell: platform === "win32",
		stdio: "inherit",
	});
	if (result.error) {
		throw new Error(`Could not launch pnpm: ${result.error.message}`);
	}
	if (result.status === null) {
		throw new Error(`vitest did not exit normally${result.signal ? ` (${result.signal})` : ""}.`);
	}
	return result.status;
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	try {
		process.exitCode = runCoverageCheck();
	} catch (error) {
		process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
		process.exitCode = 1;
	}
}

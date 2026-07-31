import { spawnSync } from "node:child_process";

export function runPnpm(args, {
	allowedStatuses = [0],
	cwd = process.cwd(),
	platform = process.platform,
	spawn = spawnSync,
} = {}) {
	// Proven repository pattern from GenerateLicenses.mjs: pnpm is a .cmd shim on Windows,
	// so Node must invoke it through the shell there. POSIX remains shell-free.
	const result = spawn("pnpm", args, {
		cwd,
		encoding: "utf8",
		maxBuffer: 64 * 1024 * 1024,
		shell: platform === "win32",
	});
	if (result.error) {
		throw new Error(`Could not launch pnpm: ${result.error.message}`);
	}
	if (result.status === null) {
		throw new Error(`pnpm ${args.join(" ")} did not exit normally${result.signal ? ` (${result.signal})` : ""}.`);
	}
	if (!allowedStatuses.includes(result.status)) {
		throw new Error(`pnpm ${args.join(" ")} failed (exit ${result.status}): ${result.stderr || "no error output"}`);
	}
	return result;
}

export function runPnpmExec(tool, args, options) {
	return runPnpm(["exec", tool, ...args], options);
}

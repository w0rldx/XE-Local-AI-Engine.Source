import { spawnSync } from "node:child_process";

export function runChecked(
	command,
	args,
	{ allowedStatuses = [0], cwd = process.cwd(), env, input, shell = false, spawn = spawnSync } = {},
) {
	const result = spawn(command, args, {
		cwd,
		encoding: "utf8",
		env,
		input,
		maxBuffer: 64 * 1024 * 1024,
		shell,
	});
	if (result.error) {
		throw new Error(`Could not launch ${command}: ${result.error.message}`);
	}
	if (result.status === null) {
		throw new Error(`${command} ${args.join(" ")} did not exit normally${result.signal ? ` (${result.signal})` : ""}.`);
	}
	if (!allowedStatuses.includes(result.status)) {
		throw new Error(`${command} ${args.join(" ")} failed (exit ${result.status}): ${result.stderr || "no error output"}`);
	}
	return result;
}

export function runPnpm(args, { platform = process.platform, ...options } = {}) {
	// Proven repository pattern from GenerateLicenses.mjs: pnpm is a .cmd shim on Windows,
	// so Node must invoke it through the shell there. POSIX remains shell-free.
	return runChecked("pnpm", args, { ...options, shell: platform === "win32" });
}

export function runPnpmExec(tool, args, options) {
	return runPnpm(["exec", tool, ...args], options);
}

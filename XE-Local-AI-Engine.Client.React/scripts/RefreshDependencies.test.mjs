import { refreshDependencies } from "./RefreshDependencies.mjs";
import assert from "node:assert/strict";
import test from "node:test";

function outputBuffer() {
	let value = "";
	return {
		stream: { write: (chunk) => (value += chunk) },
		read: () => value,
	};
}

test("refreshDependencies runs required stages in order", () => {
	const calls = [];
	const spawn = (command, args, options) => {
		calls.push({ command, args, options });
		return { error: undefined, signal: null, status: 0, stdout: "" };
	};
	const stdout = outputBuffer();
	const stderr = outputBuffer();

	const status = refreshDependencies({ cwd: "/react", platform: "linux", spawn, stdout: stdout.stream, stderr: stderr.stream });

	assert.equal(status, 0);
	assert.deepEqual(
		calls.map(({ command, args }) => [command, ...args]),
		[
			["pnpm", "install", "--frozen-lockfile"],
			["pnpm", "run", "openapi:check"],
			["pnpm", "run", "licenses:check"],
			["pnpm", "run", "validate"],
			["pnpm", "run", "build"],
			[
				"git",
				"status",
				"--short",
				"--untracked-files=no",
				"--",
				"openapi",
				"src/core/api/generated",
				"src/features/about/data/third-party-licenses.generated.json",
			],
		],
	);
	assert.equal(
		calls.every(({ options }) => options.cwd === "/react"),
		true,
	);
	assert.match(stdout.read(), /Dependency refresh completed successfully/);
	assert.equal(stderr.read(), "");
});

test("refreshDependencies continues after a failed stage and returns nonzero", () => {
	const calls = [];
	const spawn = (command, args) => {
		calls.push([command, ...args]);
		return {
			error: undefined,
			signal: null,
			status: args.includes("openapi:check") ? 1 : 0,
			stdout: "",
		};
	};
	const stdout = outputBuffer();
	const stderr = outputBuffer();

	const status = refreshDependencies({ spawn, stdout: stdout.stream, stderr: stderr.stream });

	assert.equal(status, 1);
	assert.equal(calls.length, 6);
	assert.deepEqual(calls.at(-2), ["pnpm", "run", "build"]);
	assert.equal(calls.at(-1)[0], "git");
	assert.match(stderr.read(), /OpenAPI drift check failed \(exit 1\); continuing/);
	assert.match(stderr.read(), /Dependency refresh failed in 1 required step/);
});

test("refreshDependencies skips dependent generation when the frozen install fails", () => {
	const calls = [];
	const spawn = (command, args) => {
		calls.push([command, ...args]);
		return { error: undefined, signal: null, status: 1, stdout: "" };
	};
	const stdout = outputBuffer();
	const stderr = outputBuffer();

	const status = refreshDependencies({ spawn, stdout: stdout.stream, stderr: stderr.stream });

	assert.equal(status, 1);
	assert.deepEqual(calls, [["pnpm", "install", "--frozen-lockfile"]]);
	assert.doesNotMatch(stdout.read(), /tracked derived files|Commit these regenerated tracked files/);
	assert.match(stderr.read(), /OpenAPI drift check skipped because the frozen dependency install failed/);
	assert.match(stderr.read(), /production build and license corpus skipped because the frozen dependency install failed/);
	assert.match(stderr.read(), /frozen dependency install: exit 1/);
});

test("refreshDependencies reports changed tracked derived files", () => {
	const spawn = (command) => ({
		error: undefined,
		signal: null,
		status: 0,
		stdout: command === "git" ? " M openapi/v1.json\nM  src/features/about/data/third-party-licenses.generated.json\n" : "",
	});
	const stdout = outputBuffer();

	const status = refreshDependencies({ spawn, stdout: stdout.stream, stderr: outputBuffer().stream });

	assert.equal(status, 0);
	assert.match(stdout.read(), /Commit these regenerated tracked files/);
	assert.match(stdout.read(), / {2}openapi\/v1\.json/);
	assert.match(stdout.read(), / {2}src\/features\/about\/data\/third-party-licenses\.generated\.json/);
});

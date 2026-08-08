import assert from "node:assert/strict";
import test from "node:test";

import { runCoverageCheck } from "./RunCoverageCheck.mjs";

test("bakes the coverage-gate variable into the child environment on every platform", () => {
	let received;
	const spawn = (command, args, options) => {
		received = { args, command, options };
		return { error: undefined, signal: null, status: 0 };
	};

	const status = runCoverageCheck({ env: { EXISTING: "kept" }, platform: "linux", spawn });

	assert.equal(status, 0);
	assert.equal(received.command, "pnpm");
	assert.deepEqual(received.args, ["exec", "vitest", "run", "--coverage"]);
	// The gate itself: vite.config.ts enforces thresholds only on the literal string "true".
	assert.equal(received.options.env.VITEST_COVERAGE_CHECK, "true");
	assert.equal(received.options.env.EXISTING, "kept");
	// Streamed, not captured — a captured test run would show nothing until it finished.
	assert.equal(received.options.stdio, "inherit");
	// POSIX must stay shell-free; only the Windows .cmd shim needs one.
	assert.equal(received.options.shell, false);
});

test("uses the Windows shell so the pnpm.cmd shim resolves", () => {
	let received;
	const spawn = (command, args, options) => {
		received = { args, command, options };
		return { error: undefined, signal: null, status: 0 };
	};

	runCoverageCheck({ platform: "win32", spawn });

	assert.equal(received.options.shell, true);
});

test("propagates a failing vitest exit code instead of reporting success", () => {
	const status = runCoverageCheck({
		platform: "linux",
		spawn: () => ({ error: undefined, signal: null, status: 1 }),
	});

	assert.equal(status, 1);
});

test("turns a launch failure or abnormal exit into a thrown error", () => {
	assert.throws(
		() => runCoverageCheck({ spawn: () => ({ error: new Error("ENOENT"), signal: null, status: null }) }),
		/Could not launch pnpm: ENOENT/,
	);
	assert.throws(
		() => runCoverageCheck({ spawn: () => ({ error: undefined, signal: "SIGKILL", status: null }) }),
		/vitest did not exit normally \(SIGKILL\)/,
	);
});

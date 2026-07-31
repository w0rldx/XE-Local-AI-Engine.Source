import assert from "node:assert/strict";
import test from "node:test";

import { runPnpmExec } from "./RunPackageTool.mjs";

test("uses the Windows shell for pnpm.cmd portability and checks status", () => {
	let received;
	const spawn = (command, args, options) => {
		received = { command, args, options };
		return { status: 0, stdout: "{}", stderr: "", error: undefined, signal: null };
	};

	runPnpmExec("knip", ["--reporter", "json"], { platform: "win32", spawn });

	assert.equal(received.command, "pnpm");
	assert.deepEqual(received.args, ["exec", "knip", "--reporter", "json"]);
	assert.equal(received.options.shell, true);
	assert.throws(
		() => runPnpmExec("knip", [], { spawn: () => ({ status: 2, stderr: "bad", stdout: "", signal: null }) }),
		/failed \(exit 2\): bad/,
	);
	assert.throws(
		() =>
			runPnpmExec("knip", [], {
				spawn: () => ({ status: null, stderr: "", stdout: "", signal: null, error: new Error("ENOENT") }),
			}),
		/Could not launch pnpm: ENOENT/,
	);
});

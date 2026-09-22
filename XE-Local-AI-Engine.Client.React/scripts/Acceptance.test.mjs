import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

const { scripts } = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8"));
const stages = ["lint", "knip", "signalr:check", "depcruise", "test:coverage:check", "test:tooling", "build:bundle"];

// Run the real package-script composition, replacing expensive leaf checks with recording processes.
function runGate(t, gate, failingStage = "") {
	const cwd = mkdtempSync(join(tmpdir(), "frontend-acceptance-"));
	t.after(() => rmSync(cwd, { recursive: true, force: true }));
	const fixtureScripts = { ...scripts };
	for (const stage of stages) {
		fixtureScripts[stage] = `node Record.mjs ${stage}`;
	}
	writeFileSync(join(cwd, "package.json"), JSON.stringify({ private: true, scripts: fixtureScripts }));
	writeFileSync(
		join(cwd, "Record.mjs"),
		'import { appendFileSync } from "node:fs";\n' +
			'appendFileSync("calls.txt", process.argv[2] + "\\n");\n' +
			"process.exit(process.argv[2] === process.env.FAILING_STAGE ? 1 : 0);\n",
	);
	const result = spawnSync("pnpm", ["run", gate], {
		cwd,
		env: { ...process.env, FAILING_STAGE: failingStage },
		encoding: "utf8",
		shell: process.platform === "win32",
		timeout: 30_000,
	});
	assert.ifError(result.error);
	assert.equal(result.signal, null, result.stderr);
	return { status: result.status, calls: readFileSync(join(cwd, "calls.txt"), "utf8").trim().split("\n") };
}

test("acceptance executes all checks once before bundling", (t) => {
	assert.deepEqual(runGate(t, "acceptance"), { status: 0, calls: stages });
});

test("every failed acceptance check stops subsequent work and bundling", (t) => {
	for (const [index, stage] of stages.entries()) {
		const result = runGate(t, "acceptance", stage);
		assert.notEqual(result.status, 0, stage);
		assert.deepEqual(result.calls, stages.slice(0, index + 1), stage);
	}
});

test("standalone build still checks lint and never bundles after failed lint", (t) => {
	assert.deepEqual(runGate(t, "build"), { status: 0, calls: ["lint", "build:bundle"] });
	const result = runGate(t, "build", "lint");
	assert.notEqual(result.status, 0);
	assert.deepEqual(result.calls, ["lint"]);
});

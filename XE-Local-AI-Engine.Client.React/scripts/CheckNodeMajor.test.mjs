import assert from "node:assert/strict";
import { spawnSync } from "node:child_process";
import { mkdtempSync, readFileSync, rmSync, symlinkSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";
import { fileURLToPath } from "node:url";

import { checkNodeMajor, readCiNodeMajor, workflowPath } from "./CheckNodeMajor.mjs";

const workflow = (clientReact, siblings = "22") => `jobs:
  backend-tests:
    steps:
      - uses: actions/setup-node@v7
        with:
          node-version: ${siblings}
  client-react:
    steps:
      - uses: actions/setup-node@v7
        with:
          node-version: ${clientReact}
          cache: pnpm
`;

test("reads the client-react Node major in its common spellings", () => {
	assert.equal(readCiNodeMajor(workflow("22")), 22);
	assert.equal(readCiNodeMajor(workflow('"22.13.0"', "'22.x'")), 22);
});

test("the committed workflow yields one major", () => {
	assert.ok(Number.isInteger(readCiNodeMajor(readFileSync(workflowPath, "utf8"))));
});

test("fails clearly when the value is missing, unreadable or inconsistent", () => {
	assert.throws(
		() => readCiNodeMajor("jobs:\n  client-react:\n    steps: []\n"),
		/No node-version found in the client-react job/,
	);
	assert.throws(
		() => readCiNodeMajor(workflow("lts/*")),
		/Cannot read a Node major from node-version 'lts\/\*' in job 'client-react'/,
	);
	assert.throws(() => readCiNodeMajor(workflow("22", "24")), /disagree on node-version: backend-tests=24, client-react=22/);
});

test("passes on CI's major, refuses another and names the command to run instead", () => {
	assert.deepEqual(checkNodeMajor({ runningVersion: "22.23.2", ciMajor: 22, allowDrift: false }), { status: "ok" });
	const refused = checkNodeMajor({ runningVersion: "24.21.0", ciMajor: 22, allowDrift: false });
	assert.equal(refused.status, "fail");
	assert.match(refused.message, /Running Node 24\.21\.0 \(major 24\); CI runs acceptance on Node 22\./);
	assert.match(refused.message, /mise exec node@22 -- pnpm run acceptance/);
	assert.match(refused.message, /XE_ALLOW_NODE_DRIFT=1/);
});

test("run through a symlink, the entry point still reaches a verdict", (t) => {
	const directory = mkdtempSync(join(tmpdir(), "xe-node-major-"));
	t.after(() => rmSync(directory, { recursive: true, force: true }));
	const link = join(directory, "CheckNodeMajor.mjs");
	try {
		symlinkSync(fileURLToPath(new URL("./CheckNodeMajor.mjs", import.meta.url)), link);
	} catch (error) {
		// Windows refuses symlinks without Developer Mode or the symlink privilege: skip visibly, never pass silently.
		if (error?.code === "EPERM" || error?.code === "EACCES") {
			t.skip(
				`this platform refused to create a symlink (${error.code}); enable Developer Mode or the symlink privilege to run it`,
			);
			return;
		}
		throw error;
	}
	const result = spawnSync(process.execPath, [link], { encoding: "utf8", env: { ...process.env, XE_ALLOW_NODE_DRIFT: "" } });
	assert.match(`${result.stdout}${result.stderr}`, /matches CI's Node|Running Node/);
});

test("the override continues with a loud warning, never silently", () => {
	const result = checkNodeMajor({ runningVersion: "24.21.0", ciMajor: 22, allowDrift: true });
	assert.equal(result.status, "warn");
	assert.match(result.message, /^WARNING: .*NOT CI evidence/);
});

import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import { evaluateBundleBudget, measureJavaScriptAssets } from "./CheckBundleBudget.mjs";

test("recursively measures deployed application, worker, and ORT js/mjs", (context) => {
	const root = mkdtempSync(join(tmpdir(), "xe-bundle-"));
	context.after(() => rmSync(root, { recursive: true, force: true }));
	mkdirSync(join(root, "assets", "nested"), { recursive: true });
	mkdirSync(join(root, "ort"), { recursive: true });
	writeFileSync(join(root, "assets", "app.js"), "a".repeat(10));
	writeFileSync(join(root, "assets", "nested", "feature.mjs"), "b".repeat(20));
	writeFileSync(join(root, "assets", "TtsWorker.js"), "c".repeat(30));
	writeFileSync(join(root, "ort", "ort.webgpu.mjs"), "d".repeat(40));
	writeFileSync(join(root, "ort", "runtime-worker.mjs"), "e".repeat(50));
	writeFileSync(join(root, "ort", "ignored.wasm"), "f".repeat(100));

	const measurements = measureJavaScriptAssets(root);

	assert.equal(measurements.applicationJavaScriptBytes, 30);
	assert.equal(measurements.workerJavaScriptBytes, 30);
	assert.equal(measurements.ortJavaScriptBytes, 90);
	assert.deepEqual(evaluateBundleBudget(measurements, { applicationJavaScriptBytes: 29 }), [
		{ name: "applicationJavaScriptBytes", limit: 29, actual: 30 },
	]);
});

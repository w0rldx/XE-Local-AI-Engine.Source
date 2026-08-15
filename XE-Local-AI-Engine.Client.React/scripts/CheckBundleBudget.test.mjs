import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import { evaluateBundleBudget, measureJavaScriptAssets } from "./CheckBundleBudget.mjs";

test("recursively measures all deployed js/mjs", (context) => {
	const root = mkdtempSync(join(tmpdir(), "xe-bundle-"));
	context.after(() => rmSync(root, { recursive: true, force: true }));
	mkdirSync(join(root, "assets", "nested"), { recursive: true });
	mkdirSync(join(root, "runtime"), { recursive: true });
	writeFileSync(join(root, "assets", "app.js"), "a".repeat(10));
	writeFileSync(join(root, "assets", "nested", "feature.mjs"), "b".repeat(20));
	writeFileSync(join(root, "assets", "feature-worker.js"), "c".repeat(30));
	writeFileSync(join(root, "runtime", "helper.mjs"), "d".repeat(40));
	writeFileSync(join(root, "runtime", "background-worker.mjs"), "e".repeat(50));
	writeFileSync(join(root, "runtime", "ignored.wasm"), "f".repeat(100));
	writeFileSync(join(root, "assets", "monaco-editor-Ab12Cd34.js"), "g".repeat(1000));
	writeFileSync(join(root, "assets", "editor.worker-Ef56Gh78.js"), "h".repeat(200));
	writeFileSync(join(root, "assets", "MonacoCodeEditor-Ij90Kl12.js"), "i".repeat(7));

	const measurements = measureJavaScriptAssets(root);

	assert.equal(measurements.applicationJavaScriptBytes, 150);
	assert.equal(measurements.lazyEditorJavaScriptBytes, 1207);
	assert.deepEqual(
		evaluateBundleBudget(measurements, { applicationJavaScriptBytes: 149, lazyEditorJavaScriptBytes: 1207 }),
		[{ name: "applicationJavaScriptBytes", limit: 149, actual: 150 }],
	);
	assert.deepEqual(evaluateBundleBudget(measurements, { lazyEditorJavaScriptBytes: 1206 }), [
		{ name: "lazyEditorJavaScriptBytes", limit: 1206, actual: 1207 },
	]);
});

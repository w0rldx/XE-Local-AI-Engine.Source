import assert from "node:assert/strict";
import { mkdtempSync, mkdirSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

import { evaluateBundleBudget, findEagerlyLoadedLazyAssets, measureJavaScriptAssets } from "./CheckBundleBudget.mjs";

test("flags lazy editor and scene viewer assets that index.html loads eagerly", () => {
	const indexHtml = [
		'<script type="module" crossorigin src="/assets/index-Ab12.js"></script>',
		'<link rel="modulepreload" crossorigin href="/assets/preload-helper-Cd34.js">',
		'<link rel="modulepreload" crossorigin href="/assets/monaco-editor-Ef56.js">',
		'<link rel="modulepreload" crossorigin href="/assets/three-Gh78.js">',
		'<link rel="stylesheet" crossorigin href="/assets/index-Ij90.css">',
	].join("\n");

	assert.deepEqual(findEagerlyLoadedLazyAssets(indexHtml), ["/assets/monaco-editor-Ef56.js", "/assets/three-Gh78.js"]);
	assert.deepEqual(findEagerlyLoadedLazyAssets(indexHtml.replace(/.*(monaco|three).*\n?/g, "")), []);
});

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
	writeFileSync(join(root, "assets", "three-Mn34Op56.js"), "j".repeat(500));
	writeFileSync(join(root, "assets", "BenchmarkSceneViewer-Qr78St90.js"), "k".repeat(3));

	const measurements = measureJavaScriptAssets(root);

	assert.equal(measurements.applicationJavaScriptBytes, 150);
	assert.equal(measurements.lazyEditorJavaScriptBytes, 1207);
	assert.equal(measurements.lazySceneViewerJavaScriptBytes, 503);
	assert.deepEqual(evaluateBundleBudget(measurements, { applicationJavaScriptBytes: 149, lazyEditorJavaScriptBytes: 1207 }), [
		{ name: "applicationJavaScriptBytes", limit: 149, actual: 150 },
	]);
	assert.deepEqual(evaluateBundleBudget(measurements, { lazyEditorJavaScriptBytes: 1206 }), [
		{ name: "lazyEditorJavaScriptBytes", limit: 1206, actual: 1207 },
	]);
	assert.deepEqual(evaluateBundleBudget(measurements, { lazySceneViewerJavaScriptBytes: 502 }), [
		{ name: "lazySceneViewerJavaScriptBytes", limit: 502, actual: 503 },
	]);
});

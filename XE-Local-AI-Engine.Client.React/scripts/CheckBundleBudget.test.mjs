import {
	checkBundleBudget,
	evaluateBundleBudget,
	findEagerlyLoadedLazyAssets,
	measureJavaScriptAssets,
} from "./CheckBundleBudget.mjs";
import assert from "node:assert/strict";
import { mkdirSync, mkdtempSync, rmSync, writeFileSync } from "node:fs";
import { tmpdir } from "node:os";
import { join } from "node:path";
import test from "node:test";

test("flags lazy editor, scene viewer and model lab assets that index.html loads eagerly", () => {
	const indexHtml = [
		'<script type="module" crossorigin src="/assets/index-Ab12.js"></script>',
		'<link rel="modulepreload" crossorigin href="/assets/preload-helper-Cd34.js">',
		'<link rel="modulepreload" crossorigin href="/assets/monaco-editor-Ef56.js">',
		'<link rel="modulepreload" crossorigin href="/assets/three-Gh78.js">',
		'<link rel="modulepreload" crossorigin href="/assets/model-lab-Kl12.js">',
		'<link rel="modulepreload" crossorigin href="/assets/ModelInspectPanel-Mn34.js">',
		'<link rel="modulepreload" crossorigin href="/assets/ChatMathPlugins-Op56.js">',
		'<link rel="stylesheet" crossorigin href="/assets/index-Ij90.css">',
	].join("\n");

	assert.deepEqual(findEagerlyLoadedLazyAssets(indexHtml), [
		"/assets/monaco-editor-Ef56.js",
		"/assets/three-Gh78.js",
		"/assets/model-lab-Kl12.js",
		"/assets/ModelInspectPanel-Mn34.js",
		"/assets/ChatMathPlugins-Op56.js",
	]);
	assert.deepEqual(
		findEagerlyLoadedLazyAssets(indexHtml.replace(/.*(monaco|three|model-lab|ModelInspect|ChatMath).*\n?/g, "")),
		[],
	);
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
	writeFileSync(join(root, "assets", "model-lab-Uv12Wx34.js"), "l".repeat(60));
	writeFileSync(join(root, "assets", "ModelInspectPanel-Yz56Ab78.js"), "m".repeat(5));
	writeFileSync(join(root, "assets", "ChatMathPlugins-Cd90Ef12.js"), "n".repeat(70));

	const measurements = measureJavaScriptAssets(root);

	assert.equal(measurements.applicationJavaScriptBytes, 150);
	assert.equal(measurements.lazyEditorJavaScriptBytes, 1207);
	assert.equal(measurements.lazySceneViewerJavaScriptBytes, 503);
	assert.equal(measurements.lazyModelLabJavaScriptBytes, 65);
	assert.equal(measurements.lazyMathJavaScriptBytes, 70);
	assert.deepEqual(evaluateBundleBudget(measurements, { applicationJavaScriptBytes: 149, lazyEditorJavaScriptBytes: 1207 }), [
		{ name: "applicationJavaScriptBytes", limit: 149, actual: 150 },
	]);
	assert.deepEqual(evaluateBundleBudget(measurements, { lazyEditorJavaScriptBytes: 1206 }), [
		{ name: "lazyEditorJavaScriptBytes", limit: 1206, actual: 1207 },
	]);
	assert.deepEqual(evaluateBundleBudget(measurements, { lazySceneViewerJavaScriptBytes: 502 }), [
		{ name: "lazySceneViewerJavaScriptBytes", limit: 502, actual: 503 },
	]);
	assert.deepEqual(evaluateBundleBudget(measurements, { lazyModelLabJavaScriptBytes: 64 }), [
		{ name: "lazyModelLabJavaScriptBytes", limit: 64, actual: 65 },
	]);
});

// A built app: index.html loads the entry and modulepreloads a vendor chunk; the route chunk is fetched on navigation.
function writeBuild(
	context,
	{ entryBytes, vendorBytes, routeBytes },
	budget = { headroomPercent: 2, eagerJavaScriptBytes: 150, applicationJavaScriptBytes: 1_000 },
) {
	const root = mkdtempSync(join(tmpdir(), "xe-bundle-"));
	context.after(() => rmSync(root, { recursive: true, force: true }));
	mkdirSync(join(root, "assets"));
	writeFileSync(
		join(root, "index.html"),
		[
			'<script type="module" crossorigin src="/assets/index-Ab12.js"></script>',
			'<link rel="modulepreload" crossorigin href="/assets/vendor-Cd34.js">',
			'<link rel="stylesheet" crossorigin href="/assets/index-Ef56.css">',
		].join("\n"),
	);
	writeFileSync(join(root, "assets", "index-Ab12.js"), "a".repeat(entryBytes));
	writeFileSync(join(root, "assets", "vendor-Cd34.js"), "b".repeat(vendorBytes));
	writeFileSync(join(root, "assets", "index-Ef56.css"), "c".repeat(500));
	writeFileSync(join(root, "assets", "route-Gh78.js"), "d".repeat(routeBytes));
	const budgetPath = join(root, "bundle-budget.json");
	writeFileSync(budgetPath, JSON.stringify(budget));
	return { distDirectory: root, budgetPath };
}

test("fails when the scripts index.html loads grow past the eager budget, though the application total fits", (context) => {
	const { measurements, violations } = checkBundleBudget(
		writeBuild(context, { entryBytes: 100, vendorBytes: 60, routeBytes: 300 }),
	);

	assert.equal(measurements.eagerJavaScriptBytes, 160);
	assert.equal(measurements.applicationJavaScriptBytes, 460);
	assert.deepEqual(violations, [{ name: "eagerJavaScriptBytes", limit: 150, actual: 160 }]);
});

test("passes a route chunk that grows within the application headroom without a budget edit", (context) => {
	const { measurements, violations, headroomPercent } = checkBundleBudget(
		writeBuild(context, { entryBytes: 90, vendorBytes: 50, routeBytes: 800 }),
	);

	assert.equal(measurements.eagerJavaScriptBytes, 140);
	assert.equal(measurements.applicationJavaScriptBytes, 940);
	assert.equal(headroomPercent, 2);
	assert.deepEqual(violations, []);
});

test("defaults the headroom to zero when the budget file names none", (context) => {
	const { headroomPercent, violations } = checkBundleBudget(
		writeBuild(context, { entryBytes: 90, vendorBytes: 50, routeBytes: 800 }, { applicationJavaScriptBytes: 1_000 }),
	);

	assert.equal(headroomPercent, 0);
	assert.deepEqual(violations, []);
});

test("fails on a budget key that nothing measures, instead of never enforcing it", (context) => {
	const build = writeBuild(
		context,
		{ entryBytes: 90, vendorBytes: 50, routeBytes: 800 },
		{ headroomPercent: 2, applicationJavaScriptBytes: 1_000, lazyEditorJavascriptBytes: 1 },
	);

	assert.throws(() => checkBundleBudget(build), /budgets lazyEditorJavascriptBytes, which this check does not measure/);
});

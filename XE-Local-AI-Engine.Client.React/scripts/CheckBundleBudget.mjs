import { readdirSync, readFileSync, statSync } from "node:fs";
import { join, relative, resolve, sep } from "node:path";
import { fileURLToPath } from "node:url";

function collectJavaScriptAssets(root, directory = root) {
	return readdirSync(directory, { withFileTypes: true }).flatMap((entry) => {
		const path = join(directory, entry.name);
		if (entry.isDirectory()) {
			return collectJavaScriptAssets(root, path);
		}
		if (!entry.isFile() || !/\.(?:js|mjs)$/.test(entry.name)) {
			return [];
		}
		return [{ name: relative(root, path).split(sep).join("/"), bytes: statSync(path).size }];
	});
}

// The Monaco editor core and its worker are only fetched when a CodeEditor first mounts (src/core/ui/components/
// CodeEditor), never on app boot. They are measured under their own budget so the app budget keeps its sensitivity to
// growth in code that every user downloads — one 3 MB vendor chunk would otherwise mask ~80% of it.
const lazyEditorAssetPattern = /(^|\/)(monaco-editor|editor\.worker|MonacoCodeEditor)-[^/]*\.(?:js|mjs)$/;
// `three` and the benchmark scene viewer, fetched only when a run of a sceneSpec rubric is opened. Same reasoning.
const lazySceneViewerAssetPattern = /(^|\/)(three|BenchmarkSceneViewer)-[^/]*\.(?:js|mjs)$/;

export function measureJavaScriptAssets(distDirectory) {
	const assets = collectJavaScriptAssets(distDirectory);
	const sum = (subset) => subset.reduce((total, asset) => total + asset.bytes, 0);
	const lazyEditorAssets = assets.filter((asset) => lazyEditorAssetPattern.test(asset.name));
	const lazySceneViewerAssets = assets.filter((asset) => lazySceneViewerAssetPattern.test(asset.name));
	return {
		applicationJavaScriptBytes: sum(assets) - sum(lazyEditorAssets) - sum(lazySceneViewerAssets),
		lazyEditorJavaScriptBytes: sum(lazyEditorAssets),
		lazySceneViewerJavaScriptBytes: sum(lazySceneViewerAssets),
		largestAssets: assets.toSorted((left, right) => right.bytes - left.bytes).slice(0, 5),
	};
}

export function evaluateBundleBudget(measurements, budget) {
	return Object.entries(budget)
		.filter(([name, limit]) => measurements[name] > limit)
		.map(([name, limit]) => ({ name, limit, actual: measurements[name] }));
}

// A lazy asset that index.html loads or modulepreloads is downloaded on every page, so its separate budget would lie.
// This happened when the monaco chunk group captured Vite's preload helper (see the codeSplitting groups in vite.config.ts).
export function findEagerlyLoadedLazyAssets(indexHtml) {
	return [...indexHtml.matchAll(/<(?:script|link)\b[^>]*\b(?:src|href)="([^"]+)"/g)]
		.map((match) => match[1])
		.filter((url) => lazyEditorAssetPattern.test(url) || lazySceneViewerAssetPattern.test(url));
}

export function checkBundleBudget({
	distDirectory = resolve(process.cwd(), "dist"),
	budgetPath = resolve(process.cwd(), "config/bundle-budget.json"),
} = {}) {
	const budget = JSON.parse(readFileSync(budgetPath, "utf8"));
	const measurements = measureJavaScriptAssets(distDirectory);
	const eagerLazyAssets = findEagerlyLoadedLazyAssets(readFileSync(join(distDirectory, "index.html"), "utf8"));
	return { measurements, budget, violations: evaluateBundleBudget(measurements, budget), eagerLazyAssets };
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	try {
		const { measurements, violations, budget, eagerLazyAssets } = checkBundleBudget();
		const app = measurements.applicationJavaScriptBytes;
		const appBudget = budget.applicationJavaScriptBytes;
		process.stdout.write(`app: ${app} of ${appBudget} (${((appBudget - app) / 1_000).toFixed(1)} kB headroom)\n`);
		process.stdout.write(
			`Bundle budget: app ${(measurements.applicationJavaScriptBytes / 1_000_000).toFixed(2)} MB, lazy editor ${(measurements.lazyEditorJavaScriptBytes / 1_000_000).toFixed(2)} MB, lazy scene viewer ${(measurements.lazySceneViewerJavaScriptBytes / 1_000_000).toFixed(2)} MB. Largest deployed scripts:\n` +
				`${measurements.largestAssets.map((asset) => `  ${asset.name} ${(asset.bytes / 1_000).toFixed(1)} kB`).join("\n")}\n`,
		);
		if (violations.length > 0) {
			for (const violation of violations) {
				process.stderr.write(`${violation.name}: ${violation.actual} bytes exceeds ${violation.limit} bytes.\n`);
			}
			process.stderr.write("Reduce bundle growth or approve a measured budget update.\n");
			process.exitCode = 1;
		}
		if (eagerLazyAssets.length > 0) {
			process.stderr.write(
				`dist/index.html loads lazy assets on every page: ${eagerLazyAssets.join(", ")}. ` +
					"A codeSplitting group in vite.config.ts has captured a module the app shell imports (e.g. Vite's preload helper).\n",
			);
			process.exitCode = 1;
		}
	} catch (error) {
		process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
		process.exitCode = 1;
	}
}

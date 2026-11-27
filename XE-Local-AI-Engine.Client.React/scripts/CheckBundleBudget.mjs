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
// The /model-lab route and the GGUF inspector it shares with the model details dialog, fetched only when one of them opens.
const lazyModelLabAssetPattern = /(^|\/)(model-lab|ModelInspectPanel)-[^/]*\.(?:js|mjs)$/;
// KaTeX and the math plugins, fetched only when a chat message first contains math (core/chat/components/ChatMath).
const lazyMathAssetPattern = /(^|\/)ChatMathPlugins-[^/]*\.(?:js|mjs)$/;

export function measureJavaScriptAssets(distDirectory) {
	const assets = collectJavaScriptAssets(distDirectory);
	const sum = (subset) => subset.reduce((total, asset) => total + asset.bytes, 0);
	const lazyEditorAssets = assets.filter((asset) => lazyEditorAssetPattern.test(asset.name));
	const lazySceneViewerAssets = assets.filter((asset) => lazySceneViewerAssetPattern.test(asset.name));
	const lazyModelLabAssets = assets.filter((asset) => lazyModelLabAssetPattern.test(asset.name));
	const lazyMathAssets = assets.filter((asset) => lazyMathAssetPattern.test(asset.name));
	return {
		applicationJavaScriptBytes:
			sum(assets) - sum(lazyEditorAssets) - sum(lazySceneViewerAssets) - sum(lazyModelLabAssets) - sum(lazyMathAssets),
		lazyEditorJavaScriptBytes: sum(lazyEditorAssets),
		lazySceneViewerJavaScriptBytes: sum(lazySceneViewerAssets),
		lazyModelLabJavaScriptBytes: sum(lazyModelLabAssets),
		lazyMathJavaScriptBytes: sum(lazyMathAssets),
		largestAssets: assets.toSorted((left, right) => right.bytes - left.bytes).slice(0, 5),
	};
}

export function evaluateBundleBudget(measurements, budget) {
	return Object.entries(budget)
		.filter(([name, limit]) => measurements[name] > limit)
		.map(([name, limit]) => ({ name, limit, actual: measurements[name] }));
}

// What index.html loads or modulepreloads, i.e. what every visit downloads before the first route renders.
function eagerUrls(indexHtml) {
	return [...indexHtml.matchAll(/<(?:script|link)\b[^>]*\b(?:src|href)="([^"]+)"/g)].map((match) => match[1]);
}

// The eager graph gets its own budget: the application total also counts every route-split chunk, so only this figure
// moves when code every user downloads grows.
function measureEagerJavaScriptBytes(distDirectory, indexHtml) {
	return eagerUrls(indexHtml)
		.filter((url) => /\.(?:js|mjs)$/.test(url))
		.reduce((total, url) => total + statSync(join(distDirectory, url.replace(/^\//, ""))).size, 0);
}

// A lazy asset that index.html loads or modulepreloads is downloaded on every page, so its separate budget would lie.
// This happened when the monaco chunk group captured Vite's preload helper (see the codeSplitting groups in vite.config.ts).
export function findEagerlyLoadedLazyAssets(indexHtml) {
	return eagerUrls(indexHtml).filter((url) =>
		[lazyEditorAssetPattern, lazySceneViewerAssetPattern, lazyModelLabAssetPattern, lazyMathAssetPattern].some((pattern) =>
			pattern.test(url),
		),
	);
}

export function checkBundleBudget({
	distDirectory = resolve(process.cwd(), "dist"),
	budgetPath = resolve(process.cwd(), "config/bundle-budget.json"),
} = {}) {
	// headroomPercent is the margin a budget update adds to the measured value, printed as the suggested limit.
	const { headroomPercent = 0, ...budget } = JSON.parse(readFileSync(budgetPath, "utf8"));
	const indexHtml = readFileSync(join(distDirectory, "index.html"), "utf8");
	const measurements = {
		...measureJavaScriptAssets(distDirectory),
		eagerJavaScriptBytes: measureEagerJavaScriptBytes(distDirectory, indexHtml),
	};
	// A budget key nothing measures would never fail (undefined > limit is false), so a typo silently drops a budget.
	const unmeasured = Object.keys(budget).filter((name) => typeof measurements[name] !== "number");
	if (unmeasured.length > 0) {
		throw new Error(`${budgetPath} budgets ${unmeasured.join(", ")}, which this check does not measure.`);
	}
	return {
		measurements,
		budget,
		headroomPercent,
		violations: evaluateBundleBudget(measurements, budget),
		eagerLazyAssets: findEagerlyLoadedLazyAssets(indexHtml),
	};
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	try {
		const { measurements, violations, budget, headroomPercent, eagerLazyAssets } = checkBundleBudget();
		for (const [name, limit] of Object.entries(budget)) {
			const actual = measurements[name];
			process.stdout.write(`${name}: ${actual} of ${limit} (${(((limit - actual) / actual) * 100).toFixed(1)}% headroom)\n`);
		}
		process.stdout.write(
			`Bundle budget: app ${(measurements.applicationJavaScriptBytes / 1_000_000).toFixed(2)} MB, lazy editor ${(measurements.lazyEditorJavaScriptBytes / 1_000_000).toFixed(2)} MB, lazy scene viewer ${(measurements.lazySceneViewerJavaScriptBytes / 1_000_000).toFixed(2)} MB, lazy model lab ${(measurements.lazyModelLabJavaScriptBytes / 1_000_000).toFixed(2)} MB, lazy math ${(measurements.lazyMathJavaScriptBytes / 1_000_000).toFixed(2)} MB. Largest deployed scripts:\n` +
				`${measurements.largestAssets.map((asset) => `  ${asset.name} ${(asset.bytes / 1_000).toFixed(1)} kB`).join("\n")}\n`,
		);
		if (violations.length > 0) {
			for (const violation of violations) {
				const suggested = Math.ceil((violation.actual * (1 + headroomPercent / 100)) / 1_000) * 1_000;
				process.stderr.write(
					`${violation.name}: ${violation.actual} bytes exceeds ${violation.limit} bytes (measured + ${headroomPercent}% = ${suggested}).\n`,
				);
			}
			process.stderr.write("Reduce bundle growth or approve a measured budget update to the suggested value.\n");
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

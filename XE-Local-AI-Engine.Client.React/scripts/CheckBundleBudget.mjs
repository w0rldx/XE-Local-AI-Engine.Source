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

function category(asset) {
	if (asset.name.startsWith("ort/") || /(?:^|\/)ort[^/]*\.(?:js|mjs)$/.test(asset.name)) {
		return "ort";
	}
	if (/worker/i.test(asset.name)) {
		return "worker";
	}
	return "application";
}

export function measureJavaScriptAssets(distDirectory) {
	const assets = collectJavaScriptAssets(distDirectory);
	const total = (kind) => assets.filter((asset) => category(asset) === kind).reduce((sum, asset) => sum + asset.bytes, 0);
	return {
		applicationJavaScriptBytes: total("application"),
		workerJavaScriptBytes: total("worker"),
		ortJavaScriptBytes: total("ort"),
		largestAssets: assets.toSorted((left, right) => right.bytes - left.bytes).slice(0, 5),
	};
}

export function evaluateBundleBudget(measurements, budget) {
	return Object.entries(budget)
		.filter(([name, limit]) => measurements[name] > limit)
		.map(([name, limit]) => ({ name, limit, actual: measurements[name] }));
}

export function checkBundleBudget({
	distDirectory = resolve(process.cwd(), "dist"),
	budgetPath = resolve(process.cwd(), "config/bundle-budget.json"),
} = {}) {
	const budget = JSON.parse(readFileSync(budgetPath, "utf8"));
	const measurements = measureJavaScriptAssets(distDirectory);
	return { measurements, violations: evaluateBundleBudget(measurements, budget) };
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	try {
		const { measurements, violations } = checkBundleBudget();
		process.stdout.write(
			`Bundle budget: app ${(measurements.applicationJavaScriptBytes / 1_000_000).toFixed(2)} MB, ` +
				`worker ${(measurements.workerJavaScriptBytes / 1_000_000).toFixed(2)} MB, ` +
				`ORT ${(measurements.ortJavaScriptBytes / 1_000_000).toFixed(2)} MB. Largest deployed scripts:\n` +
				`${measurements.largestAssets.map((asset) => `  ${asset.name} ${(asset.bytes / 1_000).toFixed(1)} kB`).join("\n")}\n`,
		);
		if (violations.length > 0) {
			for (const violation of violations) {
				process.stderr.write(`${violation.name}: ${violation.actual} bytes exceeds ${violation.limit} bytes.\n`);
			}
			process.stderr.write("Reduce bundle growth or approve a measured budget update.\n");
			process.exitCode = 1;
		}
	} catch (error) {
		process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
		process.exitCode = 1;
	}
}

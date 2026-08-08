import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { runPnpmExec } from "./RunPackageTool.mjs";

function issueSymbol(value) {
	if (typeof value === "string") {
		return value;
	}
	return value.name ?? value.file ?? value.path ?? value.specifier ?? JSON.stringify(value);
}

export function fingerprintKnipIssues(report) {
	const fingerprints = [];
	for (const issue of report.issues) {
		for (const [name, values] of Object.entries(issue)) {
			if (name === "file" || !Array.isArray(values)) {
				continue;
			}
			for (const value of values) {
				fingerprints.push(JSON.stringify([issue.file, name, issueSymbol(value)]));
			}
		}
	}
	return fingerprints.toSorted();
}

export function evaluateKnipBaseline(report, baseline) {
	const fingerprints = fingerprintKnipIssues(report);
	const known = new Set(baseline);
	return { fingerprints, additions: fingerprints.filter((fingerprint) => !known.has(fingerprint)) };
}

export function checkKnipBaseline() {
	const result = runPnpmExec("knip", ["--config", "Knip.ts", "--reporter", "json", "--no-exit-code"]);
	if (!result.stdout) {
		throw new Error("Knip produced no JSON output.");
	}

	const report = JSON.parse(result.stdout);
	const baseline = JSON.parse(readFileSync(resolve(process.cwd(), "config/knip-baseline.json"), "utf8"));
	return evaluateKnipBaseline(report, baseline);
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	try {
		const evaluation = checkKnipBaseline();
		process.stdout.write(`Knip baseline: ${evaluation.fingerprints.length} known issue fingerprints.\n`);
		if (evaluation.additions.length > 0) {
			for (const fingerprint of evaluation.additions) {
				process.stderr.write(`New Knip issue: ${fingerprint}\n`);
			}
			process.stderr.write("Remove new unused surface; fingerprints may be added only by explicit decision.\n");
			process.exitCode = 1;
		}
	} catch (error) {
		process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
		process.exitCode = 1;
	}
}

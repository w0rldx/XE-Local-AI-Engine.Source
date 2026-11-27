import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { runPnpmExec } from "./RunPackageTool.mjs";

// The default run sees tests as entries, so an export only a test imports passes there. The production run reads only
// the `!` patterns in Knip.ts (src without its tests) and reports that export; a deliberate test seam carries `@internal`.
const KNIP_RUNS = [
	{ label: "Knip", args: [], baseline: "config/knip-baseline.json", shrinkOnly: false },
	{
		label: "Knip production",
		args: ["--production", "--include", "exports,files"],
		baseline: "config/knip-production-baseline.json",
		shrinkOnly: true,
	},
];

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

/** Baseline entries the report no longer carries: a shrink-only baseline must drop them in the change that pays them. */
export function staleKnipBaselineEntries(fingerprints, baseline) {
	const current = new Set(fingerprints);
	return baseline.filter((fingerprint) => !current.has(fingerprint)).toSorted();
}

export function checkKnipBaseline(run) {
	const result = runPnpmExec("knip", ["--config", "Knip.ts", ...run.args, "--reporter", "json", "--no-exit-code"]);
	if (!result.stdout) {
		throw new Error(`${run.label} produced no JSON output.`);
	}

	const report = JSON.parse(result.stdout);
	const baseline = JSON.parse(readFileSync(resolve(process.cwd(), run.baseline), "utf8"));
	const evaluation = evaluateKnipBaseline(report, baseline);
	return { ...evaluation, stale: run.shrinkOnly ? staleKnipBaselineEntries(evaluation.fingerprints, baseline) : [] };
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	for (const run of KNIP_RUNS) {
		try {
			const evaluation = checkKnipBaseline(run);
			process.stdout.write(`${run.label} baseline: ${evaluation.fingerprints.length} known issue fingerprints.\n`);
			if (evaluation.additions.length > 0) {
				for (const fingerprint of evaluation.additions) {
					process.stderr.write(`New ${run.label} issue: ${fingerprint}\n`);
				}
				process.stderr.write("Remove new unused surface; fingerprints may be added only by explicit decision.\n");
				process.exitCode = 1;
			}
			if (evaluation.stale.length > 0) {
				for (const fingerprint of evaluation.stale) {
					process.stderr.write(`Stale ${run.label} baseline entry: ${fingerprint}\n`);
				}
				process.stderr.write(`${run.baseline} is shrink-only: delete the entries above in the change that paid them.\n`);
				process.exitCode = 1;
			}
		} catch (error) {
			process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
			process.exitCode = 1;
		}
	}
}

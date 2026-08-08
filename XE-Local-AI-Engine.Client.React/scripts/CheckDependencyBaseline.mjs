import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

import { runPnpmExec } from "./RunPackageTool.mjs";

export function fingerprintDependencyViolations(violations) {
	return violations
		.map((violation) => JSON.stringify([violation.rule.name, violation.from, violation.to]))
		.toSorted();
}

export function evaluateDependencyBaseline(summary, baseline) {
	const fingerprints = fingerprintDependencyViolations(summary.violations);
	const known = new Set(baseline);
	return {
		fingerprints,
		additions: fingerprints.filter((fingerprint) => !known.has(fingerprint)),
		errorCount: summary.error,
	};
}

export function checkDependencyBaseline() {
	const result = runPnpmExec(
		"depcruise",
		["src", "--config", ".dependency-cruiser.cjs", "--output-type", "json"],
		{ allowedStatuses: [0, 1] },
	);
	if (!result.stdout) {
		throw new Error("dependency-cruiser produced no JSON output.");
	}
	const report = JSON.parse(result.stdout);
	const baseline = JSON.parse(readFileSync(resolve(process.cwd(), "config/dependency-baseline.json"), "utf8"));
	return { report, evaluation: evaluateDependencyBaseline(report.summary, baseline) };
}

const isMain = process.argv[1] && fileURLToPath(import.meta.url) === resolve(process.argv[1]);
if (isMain) {
	try {
		const { report, evaluation } = checkDependencyBaseline();
		process.stdout.write(
			`dependency-cruiser: ${report.summary.error} errors, ${report.summary.warn} warnings, ` +
				`${evaluation.fingerprints.length} known fingerprints.\n`,
		);
		if (evaluation.errorCount > 0) {
			process.stderr.write("Enforced dependency-cruiser errors must be resolved. Run pnpm run depcruise:report for details.\n");
			process.exitCode = 1;
		} else if (evaluation.additions.length > 0) {
			for (const fingerprint of evaluation.additions) {
				process.stderr.write(`New dependency violation: ${fingerprint}\n`);
			}
			process.stderr.write("Remove new architecture debt; fingerprints may be added only by explicit decision.\n");
			process.exitCode = 1;
		}
	} catch (error) {
		process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
		process.exitCode = 1;
	}
}

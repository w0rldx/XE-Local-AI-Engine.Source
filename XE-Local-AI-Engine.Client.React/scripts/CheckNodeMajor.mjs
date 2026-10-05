import { readFileSync, realpathSync } from "node:fs";
import { resolve } from "node:path";
import { fileURLToPath } from "node:url";

// First step of `pnpm run acceptance`: refuse to run on a Node major other than the one CI's client-react job uses.
// package.json `engines` (>=22.13.0) lets a newer major through, and a green acceptance run on that major is not CI
// evidence: develop went red on 2026-10-02 from a Node-22-only runtime difference (src/test/JsdomBlobStream.ts).
// The major is read from the workflow, never hard-coded, so bumping CI's Node moves this check with it.
// XE_ALLOW_NODE_DRIFT=1 runs anyway on another major, with a warning that the run is not CI evidence.
export const workflowPath = fileURLToPath(new URL("../../.github/workflows/build-and-test.yml", import.meta.url));

// Every `node-version:` in the workflow, by job. A regex over the YAML on purpose: the file is ours, the shape is
// two-space job keys under `jobs:`, and a parser dependency for one value is not worth it.
export function readNodeVersionsByJob(workflowText) {
	const versions = [];
	let job = "";
	for (const line of workflowText.split(/\r?\n/)) {
		const jobMatch = /^ {2}([A-Za-z0-9_-]+):\s*$/.exec(line);
		if (jobMatch) {
			job = jobMatch[1];
			continue;
		}
		const versionMatch = /^\s+node-version:\s*["']?([^"'\s#]+)/.exec(line);
		if (versionMatch) {
			versions.push({ job, value: versionMatch[1] });
		}
	}
	return versions;
}

// The single Node major CI runs acceptance on. Throws when it cannot be found or the workflow is inconsistent.
export function readCiNodeMajor(workflowText) {
	const versions = readNodeVersionsByJob(workflowText);
	if (!versions.some((version) => version.job === "client-react")) {
		throw new Error("No node-version found in the client-react job of .github/workflows/build-and-test.yml.");
	}
	const majors = new Set(
		versions.map((version) => {
			const major = /^(\d+)(?:\.|$)/.exec(version.value)?.[1];
			if (!major) {
				throw new Error(`Cannot read a Node major from node-version '${version.value}' in job '${version.job}'.`);
			}
			return major;
		}),
	);
	if (majors.size !== 1) {
		const listing = versions.map((version) => `${version.job}=${version.value}`).join(", ");
		throw new Error(`Jobs in .github/workflows/build-and-test.yml disagree on node-version: ${listing}.`);
	}
	return Number([...majors][0]);
}

export function checkNodeMajor({ runningVersion, ciMajor, allowDrift }) {
	const runningMajor = Number(runningVersion.split(".")[0]);
	if (runningMajor === ciMajor) {
		return { status: "ok" };
	}
	const detail = `Running Node ${runningVersion} (major ${runningMajor}); CI runs acceptance on Node ${ciMajor}.`;
	if (allowDrift) {
		return {
			status: "warn",
			message: `WARNING: ${detail} XE_ALLOW_NODE_DRIFT=1 is set, so acceptance continues, and this run is NOT CI evidence.`,
		};
	}
	return {
		status: "fail",
		message:
			`${detail} A green run on another major is not CI evidence.\n` +
			`Run instead:  mise exec node@${ciMajor} -- pnpm run acceptance\n` +
			"To run on this major anyway, knowingly: XE_ALLOW_NODE_DRIFT=1 pnpm run acceptance",
	};
}

// Canonical paths on both sides: Node loads a symlinked entry point from its real path, so comparing against the
// lexical argv[1] would skip the check silently and exit 0 on the wrong major.
function canonical(path) {
	try {
		return realpathSync(path);
	} catch {
		return resolve(path);
	}
}
const isMain = process.argv[1] && canonical(fileURLToPath(import.meta.url)) === canonical(process.argv[1]);
if (isMain) {
	try {
		const ciMajor = readCiNodeMajor(readFileSync(workflowPath, "utf8"));
		const result = checkNodeMajor({
			runningVersion: process.versions.node,
			ciMajor,
			allowDrift: process.env.XE_ALLOW_NODE_DRIFT === "1",
		});
		if (result.status === "ok") {
			process.stdout.write(`Node ${process.versions.node} matches CI's Node ${ciMajor}.\n`);
		} else {
			process.stderr.write(`${result.message}\n`);
			if (result.status === "fail") {
				process.exitCode = 1;
			}
		}
	} catch (error) {
		process.stderr.write(`${error instanceof Error ? error.message : String(error)}\n`);
		process.exitCode = 1;
	}
}

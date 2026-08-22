import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import test from "node:test";

const packageJson = JSON.parse(readFileSync(new URL("../package.json", import.meta.url), "utf8"));
const reactDoctorPackageJson = JSON.parse(
	readFileSync(new URL("../node_modules/react-doctor/package.json", import.meta.url), "utf8"),
);
const doctorConfig = readFileSync(new URL("../doctor.config.jsonc", import.meta.url), "utf8");

function supportsProjectBaseline(projectRange, dependencyRange) {
	const baseline = projectRange
		.match(/^>=(\d+)\.(\d+)\.(\d+)$/)
		?.slice(1)
		.map(Number);
	if (!baseline) {
		return false;
	}
	return dependencyRange
		.split("||")
		.map((range) => range.trim())
		.some((range) => {
			const dependencyMinimum = range
				.match(/^>=(\d+)\.(\d+)\.(\d+)$/)
				?.slice(1)
				.map(Number);
			if (!dependencyMinimum) {
				return false;
			}
			for (let index = 0; index < baseline.length; index += 1) {
				if (dependencyMinimum[index] !== baseline[index]) {
					return dependencyMinimum[index] < baseline[index];
				}
			}
			return true;
		});
}

test("keeps React Doctor exact-pinned, engine-compatible, offline, advisory, and outside validate", () => {
	assert.equal(packageJson.devDependencies["react-doctor"], "0.9.12");
	assert.equal(packageJson.engines.node, ">=22.13.0");
	assert.equal(supportsProjectBaseline(packageJson.engines.node, reactDoctorPackageJson.engines.node), true);
	assert.equal(supportsProjectBaseline(packageJson.engines.node, "^20.19.0"), false);
	assert.equal(supportsProjectBaseline(packageJson.engines.node, ">=23.0.0"), false);
	assert.equal(
		packageJson.scripts.doctor,
		"react-doctor . --yes --no-telemetry --no-supply-chain --no-cache --no-dead-code --no-parallel --blocking none --no-color",
	);
	assert.doesNotMatch(packageJson.scripts.validate, /doctor/);
	assert.doesNotMatch(doctorConfig, /"rules"/);
});

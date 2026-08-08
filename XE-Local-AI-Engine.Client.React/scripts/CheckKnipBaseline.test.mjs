import assert from "node:assert/strict";
import test from "node:test";

import { evaluateKnipBaseline } from "./CheckKnipBaseline.mjs";

test("allows paid-down Knip debt and rejects same-count replacement debt", () => {
	const report = {
		issues: [
			{ file: "src/a.ts", exports: [{ name: "one" }, { name: "two" }], files: [] },
			{ file: "src/b.ts", exports: [{ name: "three" }], dependencies: [{ name: "unused" }] },
		],
	};

	const kept = JSON.stringify(["src/a.ts", "exports", "one"]);
	const removed = JSON.stringify(["src/removed.ts", "exports", "old"]);
	assert.deepEqual(evaluateKnipBaseline(report, [kept, removed]), {
		fingerprints: [
			JSON.stringify(["src/a.ts", "exports", "one"]),
			JSON.stringify(["src/a.ts", "exports", "two"]),
			JSON.stringify(["src/b.ts", "dependencies", "unused"]),
			JSON.stringify(["src/b.ts", "exports", "three"]),
		].sort(),
		additions: [
			JSON.stringify(["src/a.ts", "exports", "two"]),
			JSON.stringify(["src/b.ts", "dependencies", "unused"]),
			JSON.stringify(["src/b.ts", "exports", "three"]),
		].sort(),
	});
});

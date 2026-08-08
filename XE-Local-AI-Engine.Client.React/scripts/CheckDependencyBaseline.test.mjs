import assert from "node:assert/strict";
import test from "node:test";

import { evaluateDependencyBaseline } from "./CheckDependencyBaseline.mjs";

test("rejects same-count replacement dependency debt while allowing removals", () => {
	const kept = JSON.stringify(["no-cross-feature", "src/a.ts", "src/features/b.ts"]);
	const removed = JSON.stringify(["no-cross-feature", "src/old.ts", "src/features/b.ts"]);
	const replacement = JSON.stringify(["no-cross-feature", "src/new.ts", "src/features/b.ts"]);
	const summary = {
		error: 0,
		violations: [
			{ rule: { name: "no-cross-feature" }, from: "src/a.ts", to: "src/features/b.ts" },
			{ rule: { name: "no-cross-feature" }, from: "src/new.ts", to: "src/features/b.ts" },
		],
	};

	assert.deepEqual(evaluateDependencyBaseline(summary, [kept, removed]), {
		fingerprints: [kept, replacement].sort(),
		additions: [replacement],
		errorCount: 0,
	});
});

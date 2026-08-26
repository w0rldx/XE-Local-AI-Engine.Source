import { describe, expect, it } from "vitest";

import {
	benchmarkTaskItemChildren,
	benchmarkTaskItemLimits,
	defaultNiahGeneratorConfig,
	emptyBenchmarkTaskItemDraft,
	leafBenchmarkTaskItems,
	niahCaseCount,
	niahGeneratorIssue,
	parseNiahGeneratorConfig,
	pruneVerifierOverrides,
	reorderBenchmarkTaskItems,
	scorableBenchmarkTaskItems,
	toBenchmarkTaskItem,
	toBenchmarkTaskItemKind,
} from "@/features/benchmarks/models/BenchmarkTaskItems";
import { benchmarkTaskItemFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";

describe("toBenchmarkTaskItem", () => {
	it("maps every member the node sends", () => {
		const item = toBenchmarkTaskItem({
			id: "item-1",
			projectId: "project-1",
			parentItemId: "parent-1",
			index: 3,
			kind: "niahCase",
			revision: 4,
			inputHash: "v1:abc",
			isLeaf: true,
			countsTowardScore: false,
			prompt: "Find the passcode.",
			referenceAnswer: "42",
			verifierConfig: { accuracy: { expected: "42" } },
			generatorConfig: { contextTokens: [8192] },
			version: 9,
			createdAtUtc: 10,
			updatedAtUtc: 11,
		});

		expect(item).toMatchObject({
			id: "item-1",
			parentItemId: "parent-1",
			index: 3,
			kind: "niahCase",
			revision: 4,
			countsTowardScore: false,
			verifierConfig: { accuracy: { expected: "42" } },
		});
	});

	// The node's own default for an item written before the generator kinds existed.
	it("reads an unknown kind as a plain prompt", () => {
		expect(toBenchmarkTaskItemKind("somethingNew")).toBe("prompt");
		expect(toBenchmarkTaskItemKind(undefined)).toBe("prompt");
	});

	// An override map is opened by the verifier editor, which needs an object per criterion. A scalar is not one, and
	// keeping it would make the editor render a config it cannot edit.
	it("drops override entries that are not configuration objects", () => {
		const item = toBenchmarkTaskItem({
			inputHash: "v1:abc",
			prompt: "p",
			kind: "prompt",
			verifierConfig: { good: { expected: "x" }, bad: "not-an-object" },
		});

		expect(item.verifierConfig).toEqual({ good: { expected: "x" } });
	});

	it("reads an empty override map as no overrides at all", () => {
		expect(toBenchmarkTaskItem({ inputHash: "v1:a", prompt: "p", kind: "prompt", verifierConfig: {} }).verifierConfig).toBeNull();
	});
});

describe("leaf and scorable items", () => {
	const generator = benchmarkTaskItemFixture({ id: "gen", kind: "niah", isLeaf: false, index: 0 });
	const caseA = benchmarkTaskItemFixture({ id: "a", kind: "niahCase", parentItemId: "gen", countsTowardScore: false, index: 1 });
	const caseB = benchmarkTaskItemFixture({ id: "b", kind: "niahCase", parentItemId: "gen", countsTowardScore: false, index: 2 });
	const prompt = benchmarkTaskItemFixture({ id: "p", index: 3 });

	// A generator is not a run target; its cases are. This is the distinction every cap and every completeness check
	// is counted over.
	it("counts the cases and not the generator that made them", () => {
		expect(leafBenchmarkTaskItems([generator, caseA, caseB, prompt]).map((item) => item.id)).toEqual(["a", "b", "p"]);
	});

	it("leaves NIAH cases out of the scored set", () => {
		expect(scorableBenchmarkTaskItems([generator, caseA, caseB, prompt]).map((item) => item.id)).toEqual(["p"]);
	});

	it("lists a generator's cases in index order", () => {
		expect(benchmarkTaskItemChildren([caseB, generator, caseA, prompt], "gen").map((item) => item.id)).toEqual(["a", "b"]);
	});
});

describe("NIAH generator", () => {
	it("expands to one case per context length x depth", () => {
		expect(niahCaseCount({ ...defaultNiahGeneratorConfig, contextTokens: [8192, 32_768], needleDepthPercent: [10, 50, 90] })).toBe(6);
	});

	it("keeps the stored parameters and falls back to the defaults for the templates", () => {
		const parsed = parseNiahGeneratorConfig({ contextTokens: [4096], needleDepthPercent: [50] });

		expect(parsed.contextTokens).toEqual([4096]);
		expect(parsed.needleTemplate).toBe(defaultNiahGeneratorConfig.needleTemplate);
		expect(parsed.seed).toBeNull();
	});

	// A probe longer than the frozen window is truncated to it and therefore measures nothing — the node refuses at
	// expansion, naming both numbers, and so does the form.
	it("refuses a context length above the project's window", () => {
		const config = { ...defaultNiahGeneratorConfig, contextTokens: [65_536] };

		expect(niahGeneratorIssue(config, 32_768, 0)).toBe("contextTooLarge");
	});

	it("refuses an expansion that would pass the leaf-item cap", () => {
		const config = { ...defaultNiahGeneratorConfig, contextTokens: [1024, 2048], needleDepthPercent: [10, 50, 90] };

		expect(niahGeneratorIssue(config, 4096, benchmarkTaskItemLimits.maxLeafItems - 5)).toBe("caseCap");
		expect(niahGeneratorIssue(config, 4096, benchmarkTaskItemLimits.maxLeafItems - 6)).toBeNull();
	});

	it("names the empty axes before anything else", () => {
		expect(niahGeneratorIssue({ ...defaultNiahGeneratorConfig, contextTokens: [] }, 4096, 0)).toBe("contextTokensRequired");
		expect(niahGeneratorIssue({ ...defaultNiahGeneratorConfig, needleDepthPercent: [] }, 4096, 0)).toBe("depthsRequired");
		expect(niahGeneratorIssue({ ...defaultNiahGeneratorConfig, needleDepthPercent: [110] }, 4096, 0)).toBe("depthRange");
	});

	// Recall at 32k is a different measurement from answer quality; averaging the two produces neither.
	it("starts a NIAH draft off the ranked mean", () => {
		expect(emptyBenchmarkTaskItemDraft("niah").countsTowardScore).toBe(false);
		expect(emptyBenchmarkTaskItemDraft("prompt").countsTowardScore).toBe(true);
	});
});

describe("pruneVerifierOverrides", () => {
	it("drops a criterion whose override was emptied", () => {
		expect(pruneVerifierOverrides({ a: {}, b: { expected: "x" } })).toEqual({ b: { expected: "x" } });
	});

	it("reads an all-empty map as no overrides", () => {
		expect(pruneVerifierOverrides({ a: {} })).toBeNull();
		expect(pruneVerifierOverrides(null)).toBeNull();
	});
});

describe("reorderBenchmarkTaskItems", () => {
	const items = [
		benchmarkTaskItemFixture({ id: "a", index: 0 }),
		benchmarkTaskItemFixture({ id: "b", index: 1 }),
		benchmarkTaskItemFixture({ id: "c", index: 2 }),
	];

	it("names the whole new order, which is also the node's concurrency check", () => {
		expect(reorderBenchmarkTaskItems(items, "b", -1)).toEqual(["b", "a", "c"]);
		expect(reorderBenchmarkTaskItems(items, "b", 1)).toEqual(["a", "c", "b"]);
	});

	it("returns the current order unchanged at either end", () => {
		expect(reorderBenchmarkTaskItems(items, "a", -1)).toEqual(["a", "b", "c"]);
		expect(reorderBenchmarkTaskItems(items, "c", 1)).toEqual(["a", "b", "c"]);
		expect(reorderBenchmarkTaskItems(items, "missing", 1)).toEqual(["a", "b", "c"]);
	});
});

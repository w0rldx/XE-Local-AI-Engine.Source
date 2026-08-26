import { describe, expect, it } from "vitest";

import {
	benchmarkNiahCaseLabel,
	benchmarkTaskItemChildren,
	benchmarkTaskItemLimits,
	defaultNiahGeneratorConfig,
	emptyBenchmarkTaskItemDraft,
	leafBenchmarkTaskItems,
	niahCaseCount,
	niahCaseCriterionId,
	niahGeneratorIssue,
	parseNiahGeneratorConfig,
	pruneVerifierOverrides,
	reorderBenchmarkTaskItems,
	scorableBenchmarkTaskItems,
	serializeNiahGeneratorConfig,
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
		expect(
			niahCaseCount({ ...defaultNiahGeneratorConfig, contextTokens: [8192, 32_768], needleDepthPercent: [10, 50, 90] }),
		).toBe(6);
	});

	it("keeps the stored parameters and falls back to the node's own defaults", () => {
		const parsed = parseNiahGeneratorConfig({ contextTokens: [4096], needleDepthPercent: [50] });

		expect(parsed.contextTokens).toEqual([4096]);
		expect(parsed.needleTemplate).toBe(defaultNiahGeneratorConfig.needleTemplate);
		expect(parsed.criterionId).toBe("recall");
		expect(parsed.seed).toBe(0);
		expect(parsed.countsTowardScore).toBe(false);
	});

	// The generator config is inside the item's input hash, so two operators who typed the same set in different
	// orders must produce the same bytes — and therefore the same cases at the same indices.
	it("writes the axes distinct and ordered", () => {
		const written = serializeNiahGeneratorConfig({
			...defaultNiahGeneratorConfig,
			contextTokens: [32_768, 8192, 8192],
			needleDepthPercent: [90, 10],
		});

		expect(written["contextTokens"]).toEqual([8192, 32_768]);
		expect(written["needleDepthPercent"]).toEqual([10, 90]);
	});

	// The node hedges the label because the haystack is sized by an approximation that under-counts.
	it("reads a generated case's own label and nothing else's", () => {
		expect(benchmarkNiahCaseLabel(benchmarkTaskItemFixture({ kind: "niahCase", generatorConfig: { label: "~32k @ 50%" } }))).toBe(
			"~32k @ 50%",
		);
		expect(benchmarkNiahCaseLabel(benchmarkTaskItemFixture({ generatorConfig: { label: "~32k @ 50%" } }))).toBeNull();
	});

	// A probe longer than the frozen window is truncated to it and therefore measures nothing — the node refuses at
	// expansion, naming both numbers, and so does the form.
	it("refuses a context length above the project's window", () => {
		const config = { ...defaultNiahGeneratorConfig, contextTokens: [65_536] };

		expect(niahGeneratorIssue(config, 32_768, 0)).toBe("contextTooLarge");
	});

	it("refuses an expansion that would pass the leaf-item cap", () => {
		const config = { ...defaultNiahGeneratorConfig, contextTokens: [1024, 2048], needleDepthPercent: [10, 50, 90] };

		expect(niahGeneratorIssue(config, 4096, benchmarkTaskItemLimits.maxLeafItems - 5)).toBe("itemCap");
		expect(niahGeneratorIssue(config, 4096, benchmarkTaskItemLimits.maxLeafItems - 6)).toBeNull();
	});

	// The generator has a cap of its own, below the project's, and it applies before the item count is even consulted.
	it("refuses an expansion past the generator's own case cap", () => {
		const config = {
			...defaultNiahGeneratorConfig,
			contextTokens: [1024, 2048, 4096, 8192, 16_384, 32_768, 65_536],
			needleDepthPercent: [10, 50, 90],
		};

		expect(niahGeneratorIssue(config, 131_072, 0)).toBe("caseCap");
	});

	// A haystack under the floor cannot hide anything and its depths stop being distinguishable.
	it("refuses a probe under the node's length floor", () => {
		expect(niahGeneratorIssue({ ...defaultNiahGeneratorConfig, contextTokens: [256] }, 4096, 0)).toBe("contextTooSmall");
	});

	// The needle IS the passcode: a template without {code} hides nothing to find.
	it("requires both placeholders in the needle and the subject in the question", () => {
		expect(niahGeneratorIssue({ ...defaultNiahGeneratorConfig, needleTemplate: "A note about {city}." }, 32_768, 0)).toBe(
			"needleTemplate",
		);
		expect(niahGeneratorIssue({ ...defaultNiahGeneratorConfig, questionTemplate: "What is the passcode?" }, 32_768, 0)).toBe(
			"questionTemplate",
		);
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

describe("niahCaseCriterionId", () => {
	// The needle's verdict is recorded against ONE criterion, and this is the only place that names it — reading the
	// case's aggregate score instead is what turns a weighted rubric into a wrong found/missed.
	it("names the criterion the case's own exact override wrote", () => {
		const item = benchmarkTaskItemFixture({ kind: "niahCase", verifierConfig: { recall: { expected: "SW-4417" } } });

		expect(niahCaseCriterionId(item)).toBe("recall");
	});

	it("has no criterion to name for an authored item or a case that carries no override", () => {
		expect(niahCaseCriterionId(benchmarkTaskItemFixture({ verifierConfig: { recall: { expected: "x" } } }))).toBeNull();
		expect(niahCaseCriterionId(benchmarkTaskItemFixture({ kind: "niahCase" }))).toBeNull();
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

	// A generator moves with the cases it expanded into: leaving it behind its own children is an order no read path
	// expects, and the node's id-set check would not catch it because the set is unchanged.
	it("moves a generator together with its cases", () => {
		const withGenerator = [
			benchmarkTaskItemFixture({ id: "p", index: 0 }),
			benchmarkTaskItemFixture({ id: "gen", index: 1, kind: "niah", isLeaf: false }),
			benchmarkTaskItemFixture({ id: "c1", index: 2, kind: "niahCase", parentItemId: "gen" }),
			benchmarkTaskItemFixture({ id: "c2", index: 3, kind: "niahCase", parentItemId: "gen" }),
		];

		expect(reorderBenchmarkTaskItems(withGenerator, "gen", -1)).toEqual(["gen", "c1", "c2", "p"]);
	});

	it("returns the current order unchanged at either end", () => {
		expect(reorderBenchmarkTaskItems(items, "a", -1)).toEqual(["a", "b", "c"]);
		expect(reorderBenchmarkTaskItems(items, "c", 1)).toEqual(["a", "b", "c"]);
		expect(reorderBenchmarkTaskItems(items, "missing", 1)).toEqual(["a", "b", "c"]);
	});
});

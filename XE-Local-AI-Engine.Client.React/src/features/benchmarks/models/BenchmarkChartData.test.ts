import { describe, expect, it } from "vitest";

import {
	benchmarkRunLabel,
	fidelityBarSeries,
	hasChartableRuns,
	reasoningBudgetSeries,
	speedBarSeries,
	throughputScatterSeries,
} from "@/features/benchmarks/models/BenchmarkChartData";
import { noBenchmarkLaunchFacts } from "@/features/benchmarks/models/BenchmarkModels";
import type { BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import {
	benchmarkFidelityFixture,
	benchmarkRunDetailFixture,
	benchmarkRunSummaryFixture,
} from "@/features/benchmarks/models/BenchmarkTestFixtures";

// What these assert is not the arithmetic — it is which runs are allowed to become a point. A chart that plots a
// warm-up, a failed run or two incomparable measurements on one axis is wrong in a way no amount of styling fixes.

const run = (overrides: Partial<BenchmarkRunSummary> = {}): BenchmarkRunSummary =>
	benchmarkRunSummaryFixture({ primaryLaunch: { ...noBenchmarkLaunchFacts, kvCacheType: "q8_0" }, ...overrides });

describe("hasChartableRuns", () => {
	it("is false when nothing succeeded", () => {
		expect(hasChartableRuns([run({ primaryStatus: "Failed" }), run({ primaryStatus: "Queued" })])).toBe(false);
	});

	it("is false when the only succeeded run is a warm-up", () => {
		expect(hasChartableRuns([run({ isWarmup: true })])).toBe(false);
	});

	it("is true for one succeeded measured run", () => {
		expect(hasChartableRuns([run()])).toBe(true);
	});
});

describe("throughputScatterSeries", () => {
	it("groups points by repeat cohort and orders them by repeat index", () => {
		const series = throughputScatterSeries([
			run({ id: "b", repeatGroupId: "g", repeatIndex: 2, tokensPerSecond: 26 }),
			run({ id: "a", repeatGroupId: "g", repeatIndex: 1, tokensPerSecond: 24 }),
		]);

		expect(series).toHaveLength(1);
		expect(series[0]?.points.map((point) => point.repeat)).toEqual([1, 2]);
		expect(series[0]?.stats).toMatchObject({ count: 2 });
	});

	// A warm-up is the first-launch cost the repeats after it were meant not to pay: plotting it would show the spread
	// of the very thing being controlled for.
	it("leaves out warm-ups and runs that never succeeded", () => {
		const series = throughputScatterSeries([
			run({ id: "warm", repeatGroupId: "g", repeatIndex: 0, isWarmup: true, tokensPerSecond: 8 }),
			run({ id: "dead", repeatGroupId: "g", repeatIndex: 1, primaryStatus: "Failed", tokensPerSecond: 9 }),
			run({ id: "real", repeatGroupId: "g", repeatIndex: 2, tokensPerSecond: 24 }),
		]);

		expect(series[0]?.points.map((point) => point.runId)).toEqual(["real"]);
	});

	// Two runs of one model on different launch arguments are two different experiments; one cloud over both would
	// report a spread that is really a configuration difference.
	it("keeps two KV-cache types of one model in separate cohorts", () => {
		const series = throughputScatterSeries([
			run({ id: "q8", primaryLaunch: { ...noBenchmarkLaunchFacts, kvCacheType: "q8_0" } }),
			run({ id: "f16", primaryLaunch: { ...noBenchmarkLaunchFacts, kvCacheType: "f16" } }),
		]);

		expect(series).toHaveLength(2);
	});

	// The one-sample summary is kept; refusing to PRINT "± 0 (n=1)" is `formatStatSummary`'s job, and keeping the two
	// apart is what lets the chart label a lone point with its value and a cohort with its spread.
	it("keeps a one-sample summary rather than nulling it", () => {
		expect(throughputScatterSeries([run()])[0]?.stats).toMatchObject({ count: 1, stdDev: 0 });
	});
});

describe("speedBarSeries", () => {
	it("averages each model build's prefill, decode and latency", () => {
		const bars = speedBarSeries([
			run({ id: "a", tokensPerSecond: 20, throughput: { ...benchmarkRunSummaryFixture().throughput, ttftMs: 100 } }),
			run({ id: "b", tokensPerSecond: 30, throughput: { ...benchmarkRunSummaryFixture().throughput, ttftMs: 200 } }),
		]);

		expect(bars).toHaveLength(1);
		expect(bars[0]?.generationTokensPerSecond).toBe(25);
		expect(bars[0]?.ttftMs).toBe(150);
	});

	// A bar averaging a model's Q3 and Q8 would report a speed neither quant has.
	it("keeps two quants of one model apart", () => {
		const bars = speedBarSeries([run({ id: "a", primaryModelName: "m:Q3" }), run({ id: "b", primaryModelName: "m:Q8" })]);

		expect(bars.map((bar) => bar.label).sort()).toEqual(["m Q3", "m Q8"]);
	});

	it("reports null rather than zero for a metric no run measured", () => {
		const throughput = { ...benchmarkRunSummaryFixture().throughput, ttftMs: null };
		expect(speedBarSeries([run({ throughput })])[0]?.ttftMs).toBeNull();
	});
});

describe("fidelityBarSeries", () => {
	const quantRun = (quant: string, mean: number, overrides = {}) =>
		run({
			id: quant,
			primaryModelName: `unsloth/model:${quant}`,
			modelGroupKey: "unsloth/model",
			fidelity: benchmarkFidelityFixture({ perplexityMean: mean, ...overrides }),
		});

	it("puts a model's quants in one group, one bar each", () => {
		const groups = fidelityBarSeries([quantRun("Q4_K_M", 6.7977), quantRun("UD-Q3_K_XL", 6.9497)]);

		expect(groups).toHaveLength(1);
		expect(groups[0]?.bars.map((bar) => bar.quant)).toEqual(["Q4_K_M", "UD-Q3_K_XL"]);
		expect(groups[0]?.bars.map((bar) => bar.perplexityMean)).toEqual([6.7977, 6.9497]);
	});

	// Perplexity compares at a fixed window over fixed bytes and nowhere else, so a run measured differently is not a
	// bar in the same panel — it is a different question.
	it("splits a group when the corpus or the window differ", () => {
		const groups = fidelityBarSeries([
			quantRun("Q4_K_M", 6.79),
			quantRun("Q8_0", 6.6, { perplexityCorpusId: "wikitext2-raw-test@999999999999" }),
		]);

		expect(groups).toHaveLength(0);
	});

	it("drops a group with a single quant, which answers no comparison", () => {
		expect(fidelityBarSeries([quantRun("Q4_K_M", 6.79)])).toHaveLength(0);
	});

	it("plots a comparable KLD and withholds a stale one", () => {
		const groups = fidelityBarSeries([
			quantRun("Q4_K_M", 6.79, { kldState: "ok", kldMean: 0.012 }),
			quantRun("Q8_0", 6.6, { kldState: "kld-stale", kldMean: 0.004 }),
		]);

		expect(groups[0]?.bars.map((bar) => bar.kldMean)).toEqual([0.012, null]);
	});
});

describe("reasoningBudgetSeries", () => {
	const scored = (id: string, budget: number | null, quality: number | null) =>
		benchmarkRunDetailFixture({ id, reasoningBudgetTokens: budget, qualityScore: quality });

	it("is empty while every run carries the same budget, which is the ordinary case", () => {
		expect(reasoningBudgetSeries([scored("a", 2048, 70), scored("b", 2048, 80)])).toEqual([]);
	});

	it("orders the points by budget once it varies", () => {
		const points = reasoningBudgetSeries([scored("a", 4096, 90), scored("b", 1024, 60), scored("c", 2048, 75)]);

		expect(points.map((point) => point.reasoningBudgetTokens)).toEqual([1024, 2048, 4096]);
		expect(points.map((point) => point.qualityScore)).toEqual([60, 75, 90]);
	});

	it("leaves out a run with no budget or no score rather than plotting it at zero", () => {
		const points = reasoningBudgetSeries([scored("a", 1024, 60), scored("b", 4096, null), scored("c", null, 90)]);

		expect(points).toEqual([]);
	});
});

describe("benchmarkRunLabel", () => {
	it("names the base model with its quant tag", () => {
		expect(benchmarkRunLabel(run({ primaryModelName: "unsloth/model:Q4_K_M" }))).toBe("unsloth/model Q4_K_M");
	});

	it("falls back to the whole name when it carries no quant", () => {
		expect(benchmarkRunLabel(run({ primaryModelName: "model.gguf" }))).toBe("model.gguf");
	});
});

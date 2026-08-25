import { describe, expect, it } from "vitest";

import type { BenchmarkRunSummary } from "@/features/benchmarks/models/BenchmarkModels";
import { noBenchmarkRunThroughput } from "@/features/benchmarks/models/BenchmarkModels";
import {
	benchmarkRepeatCohortKey,
	benchmarkRepeatStats,
	formatLatencyMs,
	formatStatSummary,
	formatTokensPerSecond,
	hasThroughputBreakdown,
	throughputEvidenceEntries,
} from "@/features/benchmarks/models/BenchmarkThroughput";
import { benchmarkRunSummaryFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";

describe("BenchmarkThroughput", () => {
	// An unmeasured number is a dash, never a zero: a run whose runtime reported no timings must not read as a run that
	// measured zero tokens per second.
	it("renders an absent measurement as a dash rather than a number", () => {
		expect(formatTokensPerSecond(null)).toBe("—");
		expect(formatLatencyMs(null)).toBe("—");
	});

	it("keeps sub-second latencies in milliseconds and longer ones in seconds", () => {
		expect(formatLatencyMs(180.4)).toBe("180 ms");
		expect(formatLatencyMs(2500)).toBe("2.50 s");
		expect(formatTokensPerSecond(24.31)).toBe("24.3 tok/s");
	});

	it("reports a run with no timings as having no breakdown to show", () => {
		expect(hasThroughputBreakdown(noBenchmarkRunThroughput)).toBe(false);
		expect(hasThroughputBreakdown({ ...noBenchmarkRunThroughput, ttftMs: 12 })).toBe(true);
	});

	// The compare view diffs these entries with the launch-evidence machinery, so every member must appear as its own
	// prefixed row — a field missing here is a difference the compare table silently would not report.
	it("exposes every member as a prefixed comparable entry", () => {
		const entries = throughputEvidenceEntries({ ...noBenchmarkRunThroughput, promptTokens: 123, generationTokensPerSecond: 88 });

		expect(entries.map((entry) => entry.key)).toEqual([
			"throughput.ttftMs",
			"throughput.promptTokens",
			"throughput.promptTokensPerSecond",
			"throughput.generationTokens",
			"throughput.generationTokensPerSecond",
			"throughput.cachedPromptTokens",
			"throughput.segmentCount",
		]);
		expect(entries.find((entry) => entry.key === "throughput.promptTokens")?.value).toBe(123);
	});
});

// Repeats exist to answer "how much does this number move between launches", which one reading cannot. The spread is
// computed over the runs already in hand, and WHICH runs count is the whole correctness question.
describe("benchmarkRepeatStats", () => {
	const measured = (id: string, tokensPerSecond: number, overrides = {}) =>
		benchmarkRunSummaryFixture({
			id,
			tokensPerSecond,
			primaryModelName: "owner/Repo:Q4_K_M",
			primaryLaunch: { ...benchmarkRunSummaryFixture().primaryLaunch, kvCacheType: "q8_0", effectiveLaunchIdentity: "identity-a" },
			...overrides,
		});

	it("averages a cohort and reports its sample spread", () => {
		const stats = benchmarkRepeatStats([measured("a", 80), measured("b", 82), measured("c", 84)]);

		const cohort = stats.get(benchmarkRepeatCohortKey(measured("a", 80)));
		expect(cohort?.tokensPerSecond?.count).toBe(3);
		expect(cohort?.tokensPerSecond?.mean).toBe(82);
		// Sample (n-1) deviation of 80/82/84 is exactly 2.
		expect(cohort?.tokensPerSecond?.stdDev).toBe(2);
		expect(formatStatSummary(cohort?.tokensPerSecond ?? null)).toBe("82.0 ± 2.0 (n=3)");
	});

	// A warm-up is the first-launch cost the repeats after it were meant NOT to pay; averaging it in would report the
	// spread of the very thing it controls for. A failed run has no measurement to average at all.
	it("counts only succeeded, non-warm-up runs", () => {
		const stats = benchmarkRepeatStats([
			measured("warm", 20, { isWarmup: true, repeatIndex: 0 }),
			measured("failed", 20, { primaryStatus: "Failed" }),
			measured("a", 80),
			measured("b", 82),
		]);

		expect(stats.get(benchmarkRepeatCohortKey(measured("a", 80)))?.tokensPerSecond?.count).toBe(2);
		expect(stats.get(benchmarkRepeatCohortKey(measured("a", 80)))?.tokensPerSecond?.mean).toBe(81);
	});

	// Two runs of one model on different launch arguments are two experiments; averaging them would report a spread
	// that is really a configuration difference.
	it("keeps different KV types and different launch identities in different cohorts", () => {
		const stats = benchmarkRepeatStats([
			measured("a", 80),
			measured("b", 40, {
				primaryLaunch: { ...benchmarkRunSummaryFixture().primaryLaunch, kvCacheType: "f16", effectiveLaunchIdentity: "identity-a" },
			}),
			measured("c", 60, {
				primaryLaunch: { ...benchmarkRunSummaryFixture().primaryLaunch, kvCacheType: "q8_0", effectiveLaunchIdentity: "identity-b" },
			}),
		]);

		expect(stats.size).toBe(3);
	});

	// A model deleted and reinstalled, or a repo tag repointed at new weights, keeps its NAME and changes its content
	// fingerprint. Merging the two builds into one mean would report a spread that is really different weights.
	it("keeps two builds of one model name in different cohorts", () => {
		const rebuilt = measured("c", 40, { modelContentFingerprint: "v1:rebuilt" });
		const stats = benchmarkRepeatStats([measured("a", 80), measured("b", 82), rebuilt]);

		expect(stats.size).toBe(2);
		expect(stats.get(benchmarkRepeatCohortKey(measured("a", 80)))?.tokensPerSecond?.count).toBe(2);
		expect(stats.get(benchmarkRepeatCohortKey(rebuilt))?.tokensPerSecond?.mean).toBe(40);
	});

	// "± 0 (n=1)" would state a certainty a single reading does not have, so a lone run renders nothing.
	it("reports no spread below two samples", () => {
		const stats = benchmarkRepeatStats([measured("a", 80)]);

		expect(formatStatSummary(stats.get(benchmarkRepeatCohortKey(measured("a", 80)))?.tokensPerSecond ?? null)).toBeNull();
		expect(formatStatSummary(null)).toBeNull();
	});
});

describe("repeat cohorts across modes", () => {
	// A throughput group is deterministic and its spread is the machine; an answer-variance group samples and its
	// spread is the model. Averaging them would report one number describing neither — and the runs are identical in
	// every other member of the key, so nothing else would have kept them apart.
	const sampled = (id: string, repeatMode: BenchmarkRunSummary["repeatMode"], tokensPerSecond: number) =>
		benchmarkRunSummaryFixture({ id, repeatMode, tokensPerSecond, repeatIndex: 1 });

	it("never averages a sampled group together with a deterministic one", () => {
		const runs = [
			sampled("t1", "Throughput", 100),
			sampled("t2", "Throughput", 102),
			sampled("v1", "AnswerVariance", 40),
			sampled("v2", "AnswerVariance", 42),
		];

		const stats = benchmarkRepeatStats(runs);

		expect(stats.size).toBe(2);
		expect(stats.get(benchmarkRepeatCohortKey(runs[0] as BenchmarkRunSummary))?.tokensPerSecond?.mean).toBe(101);
		expect(stats.get(benchmarkRepeatCohortKey(runs[2] as BenchmarkRunSummary))?.tokensPerSecond?.mean).toBe(41);
	});

	it("keeps the mode in the cohort key", () => {
		const throughput = sampled("a", "Throughput", 10);
		const variance = sampled("b", "AnswerVariance", 10);

		expect(benchmarkRepeatCohortKey(throughput)).not.toBe(benchmarkRepeatCohortKey(variance));
	});
});

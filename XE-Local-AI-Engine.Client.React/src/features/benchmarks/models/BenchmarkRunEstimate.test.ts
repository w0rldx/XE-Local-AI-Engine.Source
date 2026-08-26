import { describe, expect, it } from "vitest";

import {
	benchmarkRunEstimate,
	formatBenchmarkDuration,
	medianBenchmarkRunDurationMs,
} from "@/features/benchmarks/models/BenchmarkRunEstimate";
import { benchmarkRunSummaryFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";

const run = benchmarkRunSummaryFixture;

describe("benchmarkRunEstimate", () => {
	// The multiplication is the whole point: a suite turns "four models" into twenty-four model loads and a dialog
	// naming only the models hides that.
	it("multiplies cells by items by repeats", () => {
		const estimate = benchmarkRunEstimate({ cellCount: 4, leafItemCount: 3, repeatCount: 2, warmup: false }, null);

		expect(estimate.runsPerItem).toBe(2);
		expect(estimate.totalRuns).toBe(24);
	});

	// A warm-up is never ranked, but it is still a model load the operator waits for.
	it("counts the warm-up as a run", () => {
		expect(benchmarkRunEstimate({ cellCount: 2, leafItemCount: 1, repeatCount: 1, warmup: true }, null).totalRuns).toBe(4);
	});

	it("extrapolates from the median run and omits the figure when there is none", () => {
		expect(benchmarkRunEstimate({ cellCount: 2, leafItemCount: 2, repeatCount: 1, warmup: false }, 1000).estimatedMs).toBe(4000);
		expect(benchmarkRunEstimate({ cellCount: 2, leafItemCount: 2, repeatCount: 1, warmup: false }, null).estimatedMs).toBeNull();
	});

	// MaxRunsPerRequest bounds the whole freeze, so this is what the node will refuse, not a soft warning.
	it("flags a request past the node's per-freeze cap", () => {
		expect(benchmarkRunEstimate({ cellCount: 6, leafItemCount: 6, repeatCount: 3, warmup: false }, null).exceedsCap).toBe(true);
		expect(benchmarkRunEstimate({ cellCount: 5, leafItemCount: 5, repeatCount: 4, warmup: false }, null).exceedsCap).toBe(false);
	});
});

describe("medianBenchmarkRunDurationMs", () => {
	it("takes the median of the completed runs", () => {
		const median = medianBenchmarkRunDurationMs([
			run({ id: "a", durationMs: 1000 }),
			run({ id: "b", durationMs: 3000 }),
			run({ id: "c", durationMs: 2000 }),
		]);

		expect(median).toBe(2000);
	});

	it("averages the two middles of an even sample", () => {
		expect(
			medianBenchmarkRunDurationMs([run({ id: "a", durationMs: 1000 }), run({ id: "b", durationMs: 2000 })]),
		).toBe(1500);
	});

	// A warm-up is the slow cold launch the repeats after it exist to avoid measuring; including it would inflate every
	// estimate by exactly the thing the operator asked to leave out.
	it("ignores warm-ups, unfinished runs and failures", () => {
		const median = medianBenchmarkRunDurationMs([
			run({ id: "warm", isWarmup: true, durationMs: 60_000 }),
			run({ id: "failed", primaryStatus: "Failed", durationMs: 60_000 }),
			run({ id: "running", primaryStatus: "Running", durationMs: null }),
			run({ id: "ok", durationMs: 5000 }),
		]);

		expect(median).toBe(5000);
	});

	it("has no median without a completed run", () => {
		expect(medianBenchmarkRunDurationMs([run({ primaryStatus: "Queued", durationMs: null })])).toBeNull();
		expect(medianBenchmarkRunDurationMs([])).toBeNull();
	});
});

describe("formatBenchmarkDuration", () => {
	it("stays coarse: an extrapolation cannot support seconds of an hour", () => {
		expect(formatBenchmarkDuration(4_530_000)).toBe("1h 15m");
		expect(formatBenchmarkDuration(750_000)).toBe("12m 30s");
		expect(formatBenchmarkDuration(45_000)).toBe("45s");
	});

	it("has nothing to format without an estimate", () => {
		expect(formatBenchmarkDuration(null)).toBeNull();
		expect(formatBenchmarkDuration(Number.NaN)).toBeNull();
	});
});

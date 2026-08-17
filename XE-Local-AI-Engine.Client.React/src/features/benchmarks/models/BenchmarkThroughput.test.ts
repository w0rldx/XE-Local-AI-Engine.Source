import { describe, expect, it } from "vitest";

import { noBenchmarkRunThroughput } from "@/features/benchmarks/models/BenchmarkModels";
import {
	formatLatencyMs,
	formatTokensPerSecond,
	hasThroughputBreakdown,
	throughputEvidenceEntries,
} from "@/features/benchmarks/models/BenchmarkThroughput";

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

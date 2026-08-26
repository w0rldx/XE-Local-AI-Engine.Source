import { describe, expect, it } from "vitest";

import {
	canMeasureFidelity,
	fidelityEvidenceEntries,
	formatKldValue,
	formatPerplexity,
	formatTopTokenAgreement,
	hasFidelityNumbers,
	isKldComparable,
} from "@/features/benchmarks/models/BenchmarkFidelity";
import { benchmarkFidelityFixture, benchmarkRunSummaryFixture } from "@/features/benchmarks/models/BenchmarkTestFixtures";

// Fidelity's whole job is separating two quants of one model by a number, so these tests are mostly about the two ways
// that can go wrong: rounding a real difference away, and showing a figure that was measured against something else.

describe("formatPerplexity", () => {
	it("keeps enough decimals to separate the live Q4_K_M / UD-Q3_K_XL pair", () => {
		// The measured pair on this box: bands [6.7237, 6.8718] and [6.8742, 7.0252], non-overlapping by 0.0024.
		// Two decimals would print both standard errors as "0.07" and both bands as touching.
		expect(formatPerplexity({ perplexityMean: 6.7977, perplexityStdErr: 0.074_05 })).toBe("6.7977 ± 0.0741");
		expect(formatPerplexity({ perplexityMean: 6.9497, perplexityStdErr: 0.0755 })).toBe("6.9497 ± 0.0755");
	});

	it("renders the mean alone when the node recorded no standard error", () => {
		expect(formatPerplexity({ perplexityMean: 6.7977, perplexityStdErr: null })).toBe("6.7977");
	});

	it("is null with no mean, so a caller can hide the cell rather than print a lone ±", () => {
		expect(formatPerplexity({ perplexityMean: null, perplexityStdErr: 0.07 })).toBeNull();
	});
});

describe("formatTopTokenAgreement / formatKldValue", () => {
	it("renders agreement as a percentage of tokens", () => {
		expect(formatTopTokenAgreement(0.9421)).toBe("94.2 %");
	});

	it("renders nothing rather than a zero for an absent measurement", () => {
		expect(formatTopTokenAgreement(null)).toBeNull();
		expect(formatKldValue(null)).toBeNull();
	});

	it("keeps four decimals of a KLD figure, which lives near zero", () => {
		expect(formatKldValue(0.012_34)).toBe("0.0123");
	});
});

describe("isKldComparable", () => {
	it("is true only for the node's explicit ok", () => {
		expect(isKldComparable({ kldState: "ok" })).toBe(true);
		expect(isKldComparable({ kldState: "none" })).toBe(false);
		expect(isKldComparable({ kldState: "kld-stale" })).toBe(false);
	});
});

describe("fidelityEvidenceEntries", () => {
	it("carries the perplexity facts a compare table needs to say two numbers are comparable", () => {
		const entries = fidelityEvidenceEntries(benchmarkFidelityFixture());
		const byKey = new Map(entries.map((entry) => [entry.key, entry.value]));

		expect(byKey.get("fidelity.perplexityMean")).toBe(6.7977);
		expect(byKey.get("fidelity.perplexityContextTokens")).toBe(512);
		expect(byKey.get("fidelity.perplexityCorpusId")).toBe("wikitext2-raw-test@abc123def456");
	});

	it("withholds a stale KLD trio, because a compare table is exactly where a stale figure would do its damage", () => {
		const entries = fidelityEvidenceEntries(
			benchmarkFidelityFixture({ kldState: "kld-stale", kldMean: 0.031, kldP99: 0.4, topTokenAgreement: 0.9 }),
		);
		const byKey = new Map(entries.map((entry) => [entry.key, entry.value]));

		expect(byKey.get("fidelity.kldState")).toBe("kld-stale");
		expect(byKey.get("fidelity.kldMean")).toBeNull();
		expect(byKey.get("fidelity.kldP99")).toBeNull();
		expect(byKey.get("fidelity.topTokenAgreement")).toBeNull();
	});

	it("passes a comparable trio through unchanged", () => {
		const entries = fidelityEvidenceEntries(benchmarkFidelityFixture({ kldState: "ok", kldMean: 0.031, kldP99: 0.4 }));
		const byKey = new Map(entries.map((entry) => [entry.key, entry.value]));

		expect(byKey.get("fidelity.kldMean")).toBe(0.031);
		expect(byKey.get("fidelity.kldP99")).toBe(0.4);
	});

	it("contributes no rows at all for a run the node never measured", () => {
		expect(fidelityEvidenceEntries(null)).toEqual([]);
	});
});

describe("hasFidelityNumbers", () => {
	it("counts a perplexity reading on its own — KLD is opt-in, not the point", () => {
		expect(hasFidelityNumbers(benchmarkFidelityFixture())).toBe(true);
	});

	it("does not count a stale KLD as something to show", () => {
		expect(
			hasFidelityNumbers(benchmarkFidelityFixture({ perplexityMean: null, kldState: "kld-stale", kldMean: 0.03 })),
		).toBe(false);
	});
});

describe("canMeasureFidelity", () => {
	it("needs a succeeded primary, because the measurement replays that run's own frozen placement", () => {
		expect(canMeasureFidelity(benchmarkRunSummaryFixture({ primaryStatus: "Failed" }))).toBe(false);
		expect(canMeasureFidelity(benchmarkRunSummaryFixture({ primaryStatus: "Succeeded" }))).toBe(true);
	});

	it("refuses while a measurement of the same run is already in flight", () => {
		for (const status of ["queued", "running"] as const) {
			expect(canMeasureFidelity(benchmarkRunSummaryFixture({ fidelity: benchmarkFidelityFixture({ status }) }))).toBe(false);
		}
	});

	it("allows a re-measure after a failed one — the previous attempt's numbers survive it", () => {
		expect(
			canMeasureFidelity(benchmarkRunSummaryFixture({ fidelity: benchmarkFidelityFixture({ status: "failed" }) })),
		).toBe(true);
	});
});

import { describe, expect, it } from "vitest";

import {
	DOWNLOAD_SAMPLE_WINDOW,
	type DownloadSample,
	appendDownloadSample,
	estimateDownloadRate,
	formatDownloadEta,
} from "@/features/models/models/DownloadRateEstimate";

const MB = 1_048_576;

describe("appendDownloadSample", () => {
	it("appends new readings newest-last", () => {
		let window: DownloadSample[] = [];
		window = appendDownloadSample(window, { completedBytes: 0, timestampMs: 1000 });
		window = appendDownloadSample(window, { completedBytes: MB, timestampMs: 2000 });

		expect(window).toHaveLength(2);
		expect(window.at(-1)).toEqual({ completedBytes: MB, timestampMs: 2000 });
	});

	it("ignores a reading that advances neither bytes nor time", () => {
		const first: DownloadSample = { completedBytes: MB, timestampMs: 2000 };
		const window = appendDownloadSample([first], { completedBytes: MB, timestampMs: 2000 });

		expect(window).toHaveLength(1);
	});

	it("caps the window to DOWNLOAD_SAMPLE_WINDOW, dropping the oldest", () => {
		let window: DownloadSample[] = [];
		for (let index = 0; index <= DOWNLOAD_SAMPLE_WINDOW + 2; index += 1) {
			window = appendDownloadSample(window, { completedBytes: index * MB, timestampMs: 1000 + index * 1000 });
		}

		expect(window).toHaveLength(DOWNLOAD_SAMPLE_WINDOW);
		// Oldest retained sample is no longer the very first reading.
		expect(window.at(0)?.completedBytes ?? 0).toBeGreaterThan(0);
	});
});

describe("estimateDownloadRate", () => {
	it("returns no estimate with fewer than two samples", () => {
		expect(estimateDownloadRate([], 100 * MB)).toEqual({ bytesPerSecond: undefined, etaSeconds: undefined });
		expect(estimateDownloadRate([{ completedBytes: MB, timestampMs: 1000 }], 100 * MB)).toEqual({
			bytesPerSecond: undefined,
			etaSeconds: undefined,
		});
	});

	it("computes a moving-average speed and ETA across the window", () => {
		// 10 MB moved over 2 seconds → 5 MB/s. 90 MB remain of a 100 MB file → 18s ETA.
		const samples: DownloadSample[] = [
			{ completedBytes: 0, timestampMs: 0 },
			{ completedBytes: 5 * MB, timestampMs: 1000 },
			{ completedBytes: 10 * MB, timestampMs: 2000 },
		];
		const result = estimateDownloadRate(samples, 100 * MB);

		expect(result.bytesPerSecond).toBeCloseTo(5 * MB, 0);
		expect(result.etaSeconds).toBeCloseTo(18, 5);
	});

	it("reports no ETA when the total size is unknown (indeterminate transfer)", () => {
		const samples: DownloadSample[] = [
			{ completedBytes: 0, timestampMs: 0 },
			{ completedBytes: 5 * MB, timestampMs: 1000 },
		];
		const result = estimateDownloadRate(samples, null);

		expect(result.bytesPerSecond).toBeCloseTo(5 * MB, 0);
		expect(result.etaSeconds).toBeUndefined();
	});

	it("reports a stalled transfer as zero speed with no misleading ETA", () => {
		const samples: DownloadSample[] = [
			{ completedBytes: 5 * MB, timestampMs: 1000 },
			{ completedBytes: 5 * MB, timestampMs: 4000 },
		];
		const result = estimateDownloadRate(samples, 100 * MB);

		expect(result.bytesPerSecond).toBe(0);
		expect(result.etaSeconds).toBeUndefined();
	});

	it("returns no estimate when timestamps do not advance", () => {
		const samples: DownloadSample[] = [
			{ completedBytes: 0, timestampMs: 5000 },
			{ completedBytes: 5 * MB, timestampMs: 5000 },
		];
		expect(estimateDownloadRate(samples, 100 * MB)).toEqual({ bytesPerSecond: undefined, etaSeconds: undefined });
	});
});

describe("formatDownloadEta", () => {
	it("returns undefined for missing/negative/non-finite values", () => {
		expect(formatDownloadEta(undefined)).toBeUndefined();
		expect(formatDownloadEta(-1)).toBeUndefined();
		expect(formatDownloadEta(Number.POSITIVE_INFINITY)).toBeUndefined();
	});

	it("formats sub-minute, minute, and hour ranges compactly", () => {
		expect(formatDownloadEta(45)).toBe("45s");
		expect(formatDownloadEta(130)).toBe("2m 10s");
		expect(formatDownloadEta(3900)).toBe("1h 5m");
	});
});

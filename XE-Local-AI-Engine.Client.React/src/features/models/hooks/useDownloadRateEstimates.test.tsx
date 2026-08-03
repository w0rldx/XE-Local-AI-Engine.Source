// @vitest-environment jsdom

import { renderHook, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
	type RateTrackedProgress,
	useDownloadRateEstimates,
} from "@/features/models/hooks/useDownloadRateEstimates";

// The hook timestamps each status-map change with Date.now(), so the sample window only advances if wall-clock time
// advances between renders. Only Date.now is stubbed — NOT the whole timer set — because `waitFor` needs real timers to
// poll; freezing them with vi.useFakeTimers() deadlocks every assertion below.
const START_MS = 1_700_000_000_000;

let clockMs = START_MS;

function advanceTo(elapsedMs: number): void {
	clockMs = START_MS + elapsedMs;
}

function progress(phase: string, completedBytes: number | null, totalBytes: number | null): RateTrackedProgress {
	return { phase, completedBytes, totalBytes };
}

describe("useDownloadRateEstimates", () => {
	beforeEach(() => {
		clockMs = START_MS;
		vi.spyOn(Date, "now").mockImplementation(() => clockMs);
	});

	afterEach(() => {
		vi.restoreAllMocks();
	});

	it("derives speed and ETA for the default 'Running' phase", async () => {
		const { result, rerender } = renderHook(
			({ statuses }: { statuses: ReadonlyMap<string, RateTrackedProgress> }) => useDownloadRateEstimates(statuses),
			{ initialProps: { statuses: new Map([["model-a", progress("Running", 1_000_000, 5_000_000)]]) } },
		);

		// One sample is not enough to state a rate honestly.
		await waitFor(() => expect(result.current.get("model-a")?.bytesPerSecond).toBeUndefined());

		advanceTo(2_000);
		rerender({ statuses: new Map([["model-a", progress("Running", 3_000_000, 5_000_000)]]) });

		await waitFor(() => {
			// 2 MB moved across 2 s → 1 MB/s; 2 MB remaining → 2 s ETA.
			expect(result.current.get("model-a")?.bytesPerSecond).toBe(1_000_000);
			expect(result.current.get("model-a")?.etaSeconds).toBe(2);
		});
	});

	// Guards the generalization this hook exists in two flavours for: the runtime-acquisition channel reports
	// "Downloading", not "Running". Before the active phase was parameterized, every sample from that channel was
	// dropped and the runtime banner rendered with no rate and no ETA.
	it("derives estimates for a non-default active phase", async () => {
		const { result, rerender } = renderHook(
			({ statuses }: { statuses: ReadonlyMap<string, RateTrackedProgress> }) =>
				useDownloadRateEstimates(statuses, "Downloading"),
			{ initialProps: { statuses: new Map([["runtime", progress("Downloading", 4_000_000, 12_000_000)]]) } },
		);

		advanceTo(4_000);
		rerender({ statuses: new Map([["runtime", progress("Downloading", 8_000_000, 12_000_000)]]) });

		await waitFor(() => {
			// 4 MB across 4 s → 1 MB/s; 4 MB remaining → 4 s ETA.
			expect(result.current.get("runtime")?.bytesPerSecond).toBe(1_000_000);
			expect(result.current.get("runtime")?.etaSeconds).toBe(4);
		});
	});

	// The non-byte-moving phases of the runtime lifecycle (Verifying/Extracting) must not be sampled, and must discard
	// the window so a retry that re-enters Downloading starts from a clean slate rather than a stale rate.
	it("drops the sample window when the tracked entry leaves the active phase", async () => {
		const { result, rerender } = renderHook(
			({ statuses }: { statuses: ReadonlyMap<string, RateTrackedProgress> }) =>
				useDownloadRateEstimates(statuses, "Downloading"),
			{ initialProps: { statuses: new Map([["runtime", progress("Downloading", 4_000_000, 12_000_000)]]) } },
		);

		advanceTo(4_000);
		rerender({ statuses: new Map([["runtime", progress("Downloading", 8_000_000, 12_000_000)]]) });
		await waitFor(() => expect(result.current.get("runtime")?.bytesPerSecond).toBe(1_000_000));

		advanceTo(5_000);
		rerender({ statuses: new Map([["runtime", progress("Verifying", null, 12_000_000)]]) });
		await waitFor(() => expect(result.current.has("runtime")).toBe(false));

		// Back into Downloading: the first sample of the fresh window cannot yet state a rate.
		advanceTo(6_000);
		rerender({ statuses: new Map([["runtime", progress("Downloading", 9_000_000, 12_000_000)]]) });
		await waitFor(() => expect(result.current.get("runtime")?.bytesPerSecond).toBeUndefined());
	});

	// An unknown total is the normal pinned-runtime case (no catalog-reported size, so nothing until Content-Length
	// lands). Speed is still stateable; an ETA is not, and must not be invented.
	it("reports speed but no ETA when the total size is unknown", async () => {
		const { result, rerender } = renderHook(
			({ statuses }: { statuses: ReadonlyMap<string, RateTrackedProgress> }) =>
				useDownloadRateEstimates(statuses, "Downloading"),
			{ initialProps: { statuses: new Map([["runtime", progress("Downloading", 1_000_000, null)]]) } },
		);

		advanceTo(1_000);
		rerender({ statuses: new Map([["runtime", progress("Downloading", 2_000_000, null)]]) });

		await waitFor(() => {
			expect(result.current.get("runtime")?.bytesPerSecond).toBe(1_000_000);
			expect(result.current.get("runtime")?.etaSeconds).toBeUndefined();
		});
	});
});

import { describe, expect, it } from "vitest";

import {
	type ImageJobProgressView,
	keepLatestImageJobProgress,
	toImageJobProgressView,
	toProgressDisplay,
} from "@/features/images/models/ImageJobProgressModels";

// A progress state with everything absent — each test sets only the fields it is about.
function progress(overrides: Partial<ImageJobProgressView>): ImageJobProgressView {
	return {
		seq: 1,
		status: "Generating",
		queuePosition: null,
		generationPhase: null,
		step: null,
		totalSteps: null,
		secondsPerIteration: null,
		estimatedRemainingMs: null,
		decodeStartedAtUtc: null,
		...overrides,
	};
}

describe("toProgressDisplay", () => {
	it("shows no countdown while the model is still loading or the prompt is being encoded", () => {
		for (const phase of ["Loading", "Encoding"] as const) {
			const display = toProgressDisplay(progress({ generationPhase: phase, estimatedRemainingMs: 5_000 }));
			expect(display.kind).toBe("preparing");
		}
	});

	it("shows the step bar and the estimate while sampling", () => {
		const display = toProgressDisplay(
			progress({ generationPhase: "Sampling", step: 12, totalSteps: 20, secondsPerIteration: 2, estimatedRemainingMs: 16_000 }),
		);

		expect(display).toEqual({ kind: "sampling", step: 12, totalSteps: 20, secondsPerIteration: 2, estimatedRemainingMs: 16_000 });
	});

	// The whole point of the phase-aware timeline: after the last step the VAE decode still has to run. A countdown
	// that survived into it would read "0s left" while the job kept going.
	it("shows finishing, never a countdown, once decoding starts", () => {
		const display = toProgressDisplay(
			progress({ generationPhase: "Decoding", step: 20, totalSteps: 20, estimatedRemainingMs: 0 }),
		);

		expect(display).toEqual({ kind: "finishing", decodeStartedAtUtc: null });
	});

	it("carries the decode start into the finishing display so the card can count up from it", () => {
		const display = toProgressDisplay(progress({ generationPhase: "Decoding", decodeStartedAtUtc: 1_000 }));

		expect(display).toEqual({ kind: "finishing", decodeStartedAtUtc: 1_000 });
	});

	it("withholds the estimate for a job still waiting behind another", () => {
		const display = toProgressDisplay(
			progress({ generationPhase: "Sampling", step: 3, totalSteps: 20, estimatedRemainingMs: 34_000, queuePosition: 2 }),
		);

		expect(display).toMatchObject({ kind: "sampling", estimatedRemainingMs: null });
	});

	it("reports the queue position and no countdown while queued", () => {
		expect(toProgressDisplay(progress({ status: "Queued", queuePosition: 3 }))).toEqual({ kind: "queued", queuePosition: 3 });
	});

	it("reports nothing before the first push or after the job ends", () => {
		expect(toProgressDisplay(null)).toEqual({ kind: "none" });
		expect(toProgressDisplay(progress({ status: "Succeeded" }))).toEqual({ kind: "none" });
	});
});

describe("toImageJobProgressView", () => {
	const push = { jobId: "job-1", phase: "Generating", seq: 3, occurredAtUtc: 5_000 };

	it("stamps the decode start from the push that reports Decoding", () => {
		expect(toImageJobProgressView({ ...push, generationPhase: "Decoding" }).decodeStartedAtUtc).toBe(5_000);
	});

	it("has no decode start outside Decoding", () => {
		expect(toImageJobProgressView({ ...push, generationPhase: "Sampling" }).decodeStartedAtUtc).toBeNull();
	});
});

describe("keepLatestImageJobProgress", () => {
	// sd-server prints nothing during decode, but any repeated Decoding push must not reset the counter to zero.
	it("keeps the first decode start across later Decoding pushes", () => {
		const first = progress({ seq: 7, generationPhase: "Decoding", decodeStartedAtUtc: 1_000 });
		const later = progress({ seq: 8, generationPhase: "Decoding", decodeStartedAtUtc: 9_000 });

		expect(keepLatestImageJobProgress(first, later)).toEqual({ ...later, decodeStartedAtUtc: 1_000 });
	});

	it("keeps the cached state when a push is not newer", () => {
		const current = progress({ seq: 5, step: 10 });

		expect(keepLatestImageJobProgress(current, progress({ seq: 5, step: 2 }))).toBe(current);
		expect(keepLatestImageJobProgress(current, progress({ seq: 4, step: 2 }))).toBe(current);
	});

	// seq is assigned at delivery, so a pair of step reports reordered before that point would both look new. The
	// server reports synchronously to prevent it; this rule keeps the client correct independently of that.
	it("rejects a step that would walk the bar backwards even with a newer seq", () => {
		const current = progress({ seq: 5, generationPhase: "Sampling", step: 10, totalSteps: 20 });
		const stale = progress({ seq: 6, generationPhase: "Sampling", step: 9, totalSteps: 20 });

		expect(keepLatestImageJobProgress(current, stale)).toBe(current);
	});

	it("accepts a forward step and any phase change", () => {
		const current = progress({ seq: 5, generationPhase: "Sampling", step: 10, totalSteps: 20 });
		const next = progress({ seq: 6, generationPhase: "Sampling", step: 11, totalSteps: 20 });
		const decoding = progress({ seq: 7, generationPhase: "Decoding", step: 20, totalSteps: 20 });

		expect(keepLatestImageJobProgress(current, next)).toBe(next);
		expect(keepLatestImageJobProgress(next, decoding)).toBe(decoding);
	});

	it("takes the first push as the baseline", () => {
		const first = progress({ seq: 0 });
		expect(keepLatestImageJobProgress(null, first)).toBe(first);
	});
});

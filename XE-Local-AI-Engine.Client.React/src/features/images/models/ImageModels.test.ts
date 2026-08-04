import { describe, expect, it } from "vitest";

import {
	type ImageJobProgressView,
	imageFormDefaults,
	imageGenerationFormSchema,
	isTerminalStatus,
	keepLatestImageJobProgress,
	toImageJobStatus,
	toProgressDisplay,
} from "@/features/images/models/ImageModels";

const validValues = {
	modelName: "sd-1.5",
	prompt: "a fox",
	width: 512,
	height: 512,
	steps: 20,
	sampler: "euler_a" as const,
	cfgScale: 7,
	seed: -1,
};

describe("imageGenerationFormSchema", () => {
	it("accepts a valid text-to-image request", () => {
		const result = imageGenerationFormSchema.safeParse(validValues);
		expect(result.success).toBe(true);
	});

	it("rejects an empty prompt", () => {
		const result = imageGenerationFormSchema.safeParse({ ...validValues, prompt: "   " });
		expect(result.success).toBe(false);
		if (!result.success) {
			expect(result.error.issues.some((issue) => issue.path[0] === "prompt")).toBe(true);
		}
	});

	it("rejects a missing model", () => {
		const result = imageGenerationFormSchema.safeParse({ ...validValues, modelName: "" });
		expect(result.success).toBe(false);
		if (!result.success) {
			expect(result.error.issues.some((issue) => issue.path[0] === "modelName")).toBe(true);
		}
	});

	it("rejects a width below the minimum", () => {
		const result = imageGenerationFormSchema.safeParse({ ...validValues, width: 32 });
		expect(result.success).toBe(false);
	});

	it("rejects a steps value above the maximum", () => {
		const result = imageGenerationFormSchema.safeParse({ ...validValues, steps: 500 });
		expect(result.success).toBe(false);
	});

	it("rejects an unknown sampler", () => {
		const result = imageGenerationFormSchema.safeParse({ ...validValues, sampler: "nope" });
		expect(result.success).toBe(false);
	});

	it("accepts the shipped form defaults once a model and prompt are set", () => {
		// The defaults intentionally start with an empty prompt (the form opens blank); a real submit supplies both a
		// model and a prompt, which then validates.
		const result = imageGenerationFormSchema.safeParse({ ...imageFormDefaults, modelName: "sd-1.5", prompt: "a fox" });
		expect(result.success).toBe(true);
	});

	it("rejects the shipped defaults while the prompt is still empty", () => {
		const result = imageGenerationFormSchema.safeParse({ ...imageFormDefaults, modelName: "sd-1.5" });
		expect(result.success).toBe(false);
	});
});

describe("image job status helpers", () => {
	it("maps known status strings through unchanged", () => {
		expect(toImageJobStatus("Generating")).toBe("Generating");
		expect(toImageJobStatus("Succeeded")).toBe("Succeeded");
	});

	it("falls back to Queued for an unknown status", () => {
		expect(toImageJobStatus("Weird")).toBe("Queued");
		expect(toImageJobStatus(null)).toBe("Queued");
	});

	it("classifies terminal vs non-terminal statuses", () => {
		expect(isTerminalStatus("Succeeded")).toBe(true);
		expect(isTerminalStatus("Failed")).toBe(true);
		expect(isTerminalStatus("Cancelled")).toBe(true);
		expect(isTerminalStatus("Queued")).toBe(false);
		expect(isTerminalStatus("Generating")).toBe(false);
	});
});

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
		const display = toProgressDisplay(progress({ generationPhase: "Decoding", step: 20, totalSteps: 20, estimatedRemainingMs: 0 }));

		expect(display).toEqual({ kind: "finishing" });
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

describe("keepLatestImageJobProgress", () => {
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

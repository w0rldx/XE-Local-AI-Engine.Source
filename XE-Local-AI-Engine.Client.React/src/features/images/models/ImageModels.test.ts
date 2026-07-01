import { describe, expect, it } from "vitest";

import {
	imageFormDefaults,
	imageGenerationFormSchema,
	isTerminalStatus,
	toImageJobStatus,
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

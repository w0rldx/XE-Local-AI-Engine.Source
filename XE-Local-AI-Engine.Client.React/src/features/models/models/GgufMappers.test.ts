import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse } from "@/core/api/generated";
import { toGgufRepositoryDetail } from "@/features/models/models/GgufMappers";
import { type GgufRepositoryFile, recommendedGgufFileName } from "@/features/models/models/GgufModels";

describe("toGgufRepositoryDetail file mapping", () => {
	it("maps the new quality/fit/recommended fields from the wire shape", () => {
		const response: XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse = {
			repoId: "owner/repo",
			files: [
				{
					fileName: "model-Q5_K_M.gguf",
					quant: "Q5_K_M",
					isDynamic: false,
					isDraft: false,
					sizeBytes: 5_000_000_000,
					qualityTier: "SweetSpot",
					fitVerdict: "Fits",
					isRecommended: true,
				},
			],
		};

		const detail = toGgufRepositoryDetail(response);

		expect(detail.files[0]).toMatchObject({
			qualityTier: "SweetSpot",
			fitVerdict: "Fits",
			isRecommended: true,
		});
	});

	it("coalesces omitted quality/fit/recommended fields to neutral defaults", () => {
		const response: XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse = {
			repoId: "owner/repo",
			files: [
				{
					fileName: "model-Q4_K_M.gguf",
					quant: "Q4_K_M",
					isDynamic: false,
					isDraft: false,
					sizeBytes: 4_000_000_000,
					qualityTier: "Balanced",
					fitVerdict: "Unknown",
					isRecommended: false,
				},
			],
		};

		const detail = toGgufRepositoryDetail(response);

		expect(detail.files[0]).toMatchObject({
			qualityTier: "Balanced",
			fitVerdict: "Unknown",
			isRecommended: false,
		});
	});
});

describe("draft-model rows", () => {
	it("carries the backend draft flag and its marked quant label through the mapper", () => {
		const response: XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse = {
			repoId: "unsloth/gemma-4-12b-it-GGUF",
			files: [
				{
					fileName: "MTP/mtp-gemma-4-12b-it-Q8_0.gguf",
					quant: "MTP-Q8_0",
					isDynamic: false,
					isDraft: true,
					sizeBytes: 400_000_000,
					qualityTier: "Balanced",
					fitVerdict: "Fits",
					isRecommended: false,
				},
			],
		};

		const detail = toGgufRepositoryDetail(response);

		expect(detail.files[0]).toMatchObject({ isDraft: true, quant: "MTP-Q8_0" });
	});

	it("treats an omitted draft flag as a base quant so an older backend hides nothing", () => {
		const detail = toGgufRepositoryDetail({
			repoId: "owner/repo",
			files: [{ fileName: "model-Q4_K_M.gguf", quant: "Q4_K_M" }],
		} as XeLocalAiEngineClientEndpointsModelFitV1InspectGgufRepositoryResponse);

		expect(detail.files[0]?.isDraft).toBe(false);
	});
});

describe("recommendedGgufFileName", () => {
	const file = (fileName: string, isRecommended: boolean, isDraft = false): GgufRepositoryFile => ({
		fileName,
		quant: fileName,
		isDynamic: false,
		isDraft,
		sizeBytes: 1,
		qualityTier: "Balanced",
		fitVerdict: "Unknown",
		isRecommended,
	});

	it("returns the recommended file's name when one is flagged", () => {
		const files = [file("a.gguf", false), file("b.gguf", true), file("c.gguf", false)];

		expect(recommendedGgufFileName(files)).toBe("b.gguf");
	});

	it("falls back to the first file when none is recommended", () => {
		const files = [file("a.gguf", false), file("b.gguf", false)];

		expect(recommendedGgufFileName(files)).toBe("a.gguf");
	});

	it("never falls back to a speculative-decoding draft", () => {
		// The live gemma-4-12b list: MTP drafters are the smallest files, so a plain files[0] fallback selected
		// a 0.4 GB drafter by default. Only an explicit click may ever select one.
		const files = [file("MTP/mtp-gemma-4-12b-it-Q8_0.gguf", false, true), file("gemma-4-12b-it-Q8_0.gguf", false)];

		expect(recommendedGgufFileName(files)).toBe("gemma-4-12b-it-Q8_0.gguf");
	});

	it("returns null when the repository lists nothing but drafts", () => {
		expect(recommendedGgufFileName([file("MTP/mtp-gemma-4-12b-it-Q8_0.gguf", false, true)])).toBeNull();
	});

	it("returns null for an empty list", () => {
		expect(recommendedGgufFileName([])).toBeNull();
	});
});

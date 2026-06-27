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
			files: [{ fileName: "model-Q4_K_M.gguf", quant: "Q4_K_M", isDynamic: false, sizeBytes: 4_000_000_000 }],
		};

		const detail = toGgufRepositoryDetail(response);

		expect(detail.files[0]).toMatchObject({
			qualityTier: "Balanced",
			fitVerdict: "Unknown",
			isRecommended: false,
		});
	});
});

describe("recommendedGgufFileName", () => {
	const file = (fileName: string, isRecommended: boolean): GgufRepositoryFile => ({
		fileName,
		quant: fileName,
		isDynamic: false,
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

	it("returns null for an empty list", () => {
		expect(recommendedGgufFileName([])).toBeNull();
	});
});

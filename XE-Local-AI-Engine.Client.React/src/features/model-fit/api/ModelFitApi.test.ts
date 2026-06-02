import { describe, expect, it } from "vitest";

import {
	type ApprovedImageDto,
	type LatestRecommendationsResponseDto,
	type ModelFitRecommendationDto,
	toApprovedImage,
	toLatestRecommendations,
	toModelFitRecommendation,
} from "@/features/model-fit/api/ModelFitApi";

describe("toApprovedImage", () => {
	it("maps an image, coalescing optional nullable fields and defaulting the purpose list", () => {
		const dto: ApprovedImageDto = {
			approvedImageId: "llmfit-recommender-0-9-30",
			displayName: "llmfit recommender",
			purpose: ["ModelRecommendation", "ModelBenchmark"],
			imageReference: "ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a519",
			enabled: true,
		};

		const image = toApprovedImage(dto);

		expect(image.description).toBeNull();
		expect(image.sourceUrl).toBeNull();
		expect(image.upstreamVersion).toBeNull();
		expect(image.deprecatedAtUtc).toBeNull();
		expect(image.lastUsedAtUtc).toBeNull();
		expect(image.diagnostics).toBeNull();
		expect(image.purpose).toEqual(["ModelRecommendation", "ModelBenchmark"]);
		expect(image.imageReference).toBe("ghcr.io/alexsjones/llmfit:0.9.30@sha256:465a519");
		expect(image.enabled).toBe(true);
	});
});

describe("toModelFitRecommendation", () => {
	it("maps a recommendation row, coalescing nullable metric fields", () => {
		const dto: ModelFitRecommendationDto = {
			rank: 1,
			modelName: "llama3.1:8b",
			score: 87.5,
			isInstalled: false,
			pullModelName: "llama3.1:8b",
		};

		const recommendation = toModelFitRecommendation(dto);

		expect(recommendation.providerModelName).toBeNull();
		expect(recommendation.fitLevel).toBeNull();
		expect(recommendation.estimatedTokensPerSecond).toBeNull();
		expect(recommendation.requiredVramMb).toBeNull();
		expect(recommendation.contextTokens).toBeNull();
		expect(recommendation.isInstalled).toBe(false);
		expect(recommendation.pullModelName).toBe("llama3.1:8b");
	});
});

describe("toLatestRecommendations", () => {
	it("maps the no-cache state with all snapshot fields null and an empty list", () => {
		const dto: LatestRecommendationsResponseDto = {
			hasCache: false,
			recommendations: [],
		};

		const view = toLatestRecommendations(dto);

		expect(view.hasCache).toBe(false);
		expect(view.snapshotId).toBeNull();
		expect(view.status).toBeNull();
		expect(view.sourceImageId).toBeNull();
		expect(view.lastRefreshedAtUtc).toBeNull();
		expect(view.recommendations).toEqual([]);
	});

	it("maps a populated snapshot with its ranked recommendations", () => {
		const dto: LatestRecommendationsResponseDto = {
			hasCache: true,
			snapshotId: "snap-1",
			status: "Succeeded",
			sourceImageId: "llmfit-recommender-0-9-30",
			useCase: "coding",
			providerName: "ollama",
			lastRefreshedAtUtc: 1000,
			recommendations: [
				{ rank: 1, modelName: "llama3.1:8b", score: 90, isInstalled: true },
				{ rank: 2, modelName: "qwen2.5:7b", score: 85, isInstalled: false },
			],
		};

		const view = toLatestRecommendations(dto);

		expect(view.hasCache).toBe(true);
		expect(view.snapshotId).toBe("snap-1");
		expect(view.status).toBe("Succeeded");
		expect(view.useCase).toBe("coding");
		expect(view.recommendations).toHaveLength(2);
		expect(view.recommendations[0]?.modelName).toBe("llama3.1:8b");
		expect(view.recommendations[1]?.rank).toBe(2);
	});

	it("defensively coalesces a missing recommendations array to empty", () => {
		const dto = { hasCache: false } as LatestRecommendationsResponseDto;

		const view = toLatestRecommendations(dto);

		expect(view.recommendations).toEqual([]);
	});
});

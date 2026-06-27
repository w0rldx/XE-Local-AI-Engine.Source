import { describe, expect, it } from "vitest";

import type {
	XeLocalAiEngineClientEndpointsModelFitV1GetLatestRecommendationsResponse,
	XeLocalAiEngineClientEndpointsModelFitV1ModelFitRecommendationResponse,
} from "@/core/api/generated/types.gen";
import { toLatestRecommendations } from "@/features/model-fit/models/ModelFitMappers";

// Minimal recommendation row factory — all generated fields are optional so only required test
// fields are passed; the mapper coalesces the rest to safe defaults.
function makeRecDto(
	overrides: Partial<XeLocalAiEngineClientEndpointsModelFitV1ModelFitRecommendationResponse> = {},
): XeLocalAiEngineClientEndpointsModelFitV1ModelFitRecommendationResponse {
	return { rank: 1, modelName: "qwen3-coder", score: 80, isInstalled: false, ...overrides };
}

function wrapInResponse(
	rec: XeLocalAiEngineClientEndpointsModelFitV1ModelFitRecommendationResponse,
): XeLocalAiEngineClientEndpointsModelFitV1GetLatestRecommendationsResponse {
	return {
		hasCache: true,
		snapshotId: "snap-1",
		status: "Succeeded",
		useCase: "coding",
		lastRefreshedAtUtc: 1_000_000,
		recommendations: [rec],
	};
}

describe("toLatestRecommendations — releaseDate and isTrustedPublisher mapping", () => {
	it("maps releaseDate from the DTO field preserving the ISO string", () => {
		const result = toLatestRecommendations(wrapInResponse(makeRecDto({ releaseDate: "2026-01-15" })));

		expect(result.recommendations[0]?.releaseDate).toBe("2026-01-15");
	});

	it("coalesces an absent releaseDate to null", () => {
		const result = toLatestRecommendations(wrapInResponse(makeRecDto()));

		expect(result.recommendations[0]?.releaseDate).toBeNull();
	});

	it("maps isTrustedPublisher false from the DTO field", () => {
		const result = toLatestRecommendations(wrapInResponse(makeRecDto({ isTrustedPublisher: false })));

		expect(result.recommendations[0]?.isTrustedPublisher).toBe(false);
	});

	it("maps isTrustedPublisher true from the DTO field", () => {
		const result = toLatestRecommendations(wrapInResponse(makeRecDto({ isTrustedPublisher: true })));

		expect(result.recommendations[0]?.isTrustedPublisher).toBe(true);
	});

	it("coalesces an absent isTrustedPublisher to false (unknown publisher = not trusted)", () => {
		const result = toLatestRecommendations(wrapInResponse(makeRecDto()));

		expect(result.recommendations[0]?.isTrustedPublisher).toBe(false);
	});
});

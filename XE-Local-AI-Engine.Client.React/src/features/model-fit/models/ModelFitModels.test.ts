import { describe, expect, it } from "vitest";

import {
	defaultModelFitProviderName,
	defaultModelFitUseCase,
	modelFitUseCases,
	modelFitUseCaseSchema,
	modelRecommendationCheckTemplateId,
} from "@/features/model-fit/models/ModelFitModels";

describe("model-fit use cases", () => {
	it("exposes exactly the six llmfit-supported use cases", () => {
		expect([...modelFitUseCases]).toEqual(["general", "coding", "reasoning", "chat", "multimodal", "embedding"]);
	});

	it("defaults to coding / ollama matching the scheduler template defaults", () => {
		expect(defaultModelFitUseCase).toBe("coding");
		expect(defaultModelFitProviderName).toBe("ollama");
	});

	it("validates each supported use case and rejects an unknown one", () => {
		for (const useCase of modelFitUseCases) {
			expect(modelFitUseCaseSchema.safeParse(useCase).success).toBe(true);
		}
		expect(modelFitUseCaseSchema.safeParse("nonsense").success).toBe(false);
	});

	it("pins the reserved scheduler template id the refresh action fires", () => {
		expect(modelRecommendationCheckTemplateId).toBe("model-recommendation-check");
	});
});

import { describe, expect, it } from "vitest";

import {
	defaultGgufQuant,
	defaultModelFitUseCase,
	modelFitUseCaseSchema,
	modelFitUseCases,
	modelRecommendationCheckTemplateId,
} from "@/features/model-fit/models/ModelFitModels";

describe("model-fit use cases", () => {
	it("exposes exactly the six llmfit-supported use cases", () => {
		expect([...modelFitUseCases]).toEqual(["general", "coding", "reasoning", "chat", "multimodal", "embedding"]);
	});

	it("defaults to the coding use case matching the scheduler template default", () => {
		expect(defaultModelFitUseCase).toBe("coding");
	});

	it("defaults the GGUF quant to the HF policy default Q4_K_M", () => {
		expect(defaultGgufQuant).toBe("Q4_K_M");
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

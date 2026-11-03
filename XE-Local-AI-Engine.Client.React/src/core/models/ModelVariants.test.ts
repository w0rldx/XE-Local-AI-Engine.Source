import { describe, expect, it } from "vitest";

import { modelVariantLabel, sameRepoVariants } from "@/core/models/ModelVariants";

describe("sameRepoVariants", () => {
	const llamacpp = (modelName: string) => ({ modelName, provider: "llamacpp" });

	it("lists the other llama.cpp variants of the repo, whatever the repo's case", () => {
		const models = [
			llamacpp("org/Tiny-GGUF:Q4_K_M"),
			llamacpp("ORG/tiny-gguf:Q3_K_XL"),
			llamacpp("org/Other-GGUF:Q4_K_M"),
			{ modelName: "org/Tiny-GGUF:latest", provider: "Ollama" },
		];

		expect(sameRepoVariants("org/Tiny-GGUF:Q4_K_M", models)).toEqual(["ORG/tiny-gguf:Q3_K_XL"]);
	});

	it("finds no repo in a name without a colon", () => {
		expect(sameRepoVariants("tiny", [llamacpp("tiny"), llamacpp("tiny:Q4")])).toEqual([]);
	});

	it("labels a variant by its quant", () => {
		expect(modelVariantLabel("org/Tiny-GGUF:Q4_K_M")).toBe("Q4_K_M");
		expect(modelVariantLabel("tiny")).toBe("tiny");
	});
});

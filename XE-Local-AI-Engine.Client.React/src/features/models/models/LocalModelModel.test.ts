import { describe, expect, it } from "vitest";

import {
	emptyModelValue,
	formatModelModifiedDate,
	formatModelSize,
	modelVariantLabel,
	sameRepoVariants,
} from "@/features/models/models/LocalModelModel";

describe("local model model helpers", () => {
	it("formats model sizes", () => {
		expect(formatModelSize(1_073_741_824)).toBe("1.0 GB");
		expect(formatModelSize(1_048_576)).toBe("1.0 MB");
		expect(formatModelSize(1024)).toBe("1.0 KB");
		expect(formatModelSize(null)).toBe(emptyModelValue);
	});

	it("formats UTC modified dates deterministically", () => {
		expect(formatModelModifiedDate(Date.UTC(2026, 4, 24))).toBe("2026-05-24");
		expect(formatModelModifiedDate(undefined)).toBe(emptyModelValue);
	});
});

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

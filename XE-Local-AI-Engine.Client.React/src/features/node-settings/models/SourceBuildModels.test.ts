import { describe, expect, it } from "vitest";

import { sourceBuildRequest, sourceBuildValidationError } from "@/features/node-settings/models/SourceBuildModels";

describe("source build models", () => {
	it("requires explicit trust for custom repositories", () => {
		expect(
			sourceBuildValidationError({
				backend: "vulkan",
				source: "custom",
				repository: "https://github.com/example/fork",
				commit: "",
				acknowledgeCustomSourceRisk: false,
			}),
		).toContain("Acknowledge");
	});

	it("creates the exact normalized request body", () => {
		expect(
			sourceBuildRequest({
				backend: "cuda",
				source: "custom",
				repository: " https://github.com/example/fork ",
				commit: "ABCDEFABCDEFABCDEFABCDEFABCDEFABCDEFABCD",
				acknowledgeCustomSourceRisk: true,
			}),
		).toEqual({
			backend: "cuda",
			source: "custom",
			repository: "https://github.com/example/fork",
			commit: "abcdefabcdefabcdefabcdefabcdefabcdefabcd",
			acknowledgeCustomSourceRisk: true,
		});
	});
});

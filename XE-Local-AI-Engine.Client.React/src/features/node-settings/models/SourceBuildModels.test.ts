import { describe, expect, it } from "vitest";

import {
	mergeSourceBuildLogs,
	sourceBuildIdentity,
	sourceBuildRequest,
	sourceBuildValidationError,
} from "@/features/node-settings/models/SourceBuildModels";

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

	it("merges persisted and live reconnect logs without duplicating their overlap", () => {
		expect(mergeSourceBuildLogs(["clone", "configure", "build"], ["configure", "build", "link"])).toEqual([
			"clone",
			"configure",
			"build",
			"link",
		]);
	});

	it("identifies a concrete run by build id", () => {
		const base = {
			buildId: "11111111-1111-4111-8111-111111111111",
			backend: "cuda" as const,
			source: "official" as const,
			repository: "https://github.com/ggml-org/llama.cpp",
			revisionMode: "enginePinned" as const,
			requestedCommit: null,
			resolvedCommit: null,
		};
		expect(sourceBuildIdentity(base)).toBe(sourceBuildIdentity({ ...base, resolvedCommit: "a".repeat(40) }));
		expect(sourceBuildIdentity(base)).not.toBe(sourceBuildIdentity({ ...base, buildId: "22222222-2222-4222-8222-222222222222" }));
	});
});

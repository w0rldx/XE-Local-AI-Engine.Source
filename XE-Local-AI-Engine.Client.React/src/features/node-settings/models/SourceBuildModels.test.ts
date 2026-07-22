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

	it("identifies a build by immutable revision intent rather than its resolved SHA", () => {
		const base = {
			backend: "cuda" as const,
			source: "official" as const,
			repository: "https://github.com/ggml-org/llama.cpp",
			revisionMode: "enginePinned" as const,
			requestedCommit: null,
			resolvedCommit: null,
		};
		expect(sourceBuildIdentity(base)).toBe(sourceBuildIdentity({ ...base, resolvedCommit: "a".repeat(40) }));
	});
});

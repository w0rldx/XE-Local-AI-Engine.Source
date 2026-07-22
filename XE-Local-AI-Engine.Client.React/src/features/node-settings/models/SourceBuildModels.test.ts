import { describe, expect, it } from "vitest";

import {
	mergeSourceBuildLogs,
	sourceBuildIdentity,
	sourceBuildPrerequisiteDiagnostic,
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

	it("retains technical diagnostics without exposing backend availability prose", () => {
		expect(
			sourceBuildPrerequisiteDiagnostic({
				key: "cmake",
				satisfied: true,
				detail: "CMake detected: cmake version 4.0.0",
			}),
		).toBe("cmake version 4.0.0");
		expect(sourceBuildPrerequisiteDiagnostic({ key: "os-is-linux", satisfied: true, detail: "Linux host detected." })).toBeNull();
		expect(
			sourceBuildPrerequisiteDiagnostic({ key: "free-disk", satisfied: false, detail: "Only 4.2 GB free; 20 GB required." }),
		).toBe("4.2 GB / 20 GB");
	});
});

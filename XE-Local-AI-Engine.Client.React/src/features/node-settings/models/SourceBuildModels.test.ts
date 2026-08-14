import { describe, expect, it } from "vitest";

import {
	mergeSourceBuildLogs,
	sourceBuildIdentity,
	sourceBuildLogEntries,
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

	it("submits an optional explicit commit for the official source", () => {
		expect(
			sourceBuildRequest({
				backend: "cpu",
				source: "official",
				repository: "",
				commit: "A".repeat(40),
				acknowledgeCustomSourceRisk: false,
			}),
		).toEqual({
			backend: "cpu",
			source: "official",
			repository: null,
			commit: "a".repeat(40),
			acknowledgeCustomSourceRisk: false,
		});
	});

	it("submits no commit for an official build without one", () => {
		expect(
			sourceBuildRequest({
				backend: "cpu",
				source: "official",
				repository: "",
				commit: "  ",
				acknowledgeCustomSourceRisk: false,
			}).commit,
		).toBeNull();
	});

	it("flags a malformed commit for the official source", () => {
		expect(
			sourceBuildValidationError({
				backend: "cpu",
				source: "official",
				repository: "",
				commit: "abc123",
				acknowledgeCustomSourceRisk: false,
			}),
		).toContain("40-character");
	});

	it("reconciles a 400-line server ring with a 2000-line live buffer by sequence", () => {
		const live = sourceBuildLogEntries(
			0,
			Array.from({ length: 2000 }, (_, sequence) => `line-${sequence}`),
		);
		const persisted = sourceBuildLogEntries(
			1600,
			Array.from({ length: 400 }, (_, index) => `line-${1600 + index}`),
		);

		const merged = mergeSourceBuildLogs(persisted, live);

		expect(merged).toHaveLength(2000);
		expect(merged[0]).toEqual({ sequence: 0, message: "line-0" });
		expect(merged.at(-1)).toEqual({ sequence: 1999, message: "line-1999" });
	});

	it("preserves repeated messages at different sequences while deduplicating replayed sequences", () => {
		expect(
			mergeSourceBuildLogs(sourceBuildLogEntries(10, ["same", "same"]), sourceBuildLogEntries(10, ["same", "same"])),
		).toEqual([
			{ sequence: 10, message: "same" },
			{ sequence: 11, message: "same" },
		]);
	});

	it("orders out-of-order and disjoint ranges numerically", () => {
		expect(
			mergeSourceBuildLogs(
				sourceBuildLogEntries(20, ["twenty"]),
				sourceBuildLogEntries(2, ["two"]),
				sourceBuildLogEntries(10, ["ten"]),
			),
		).toEqual([
			{ sequence: 2, message: "two" },
			{ sequence: 10, message: "ten" },
			{ sequence: 20, message: "twenty" },
		]);
	});

	it("retains only the newest 2000 sequenced entries", () => {
		const merged = mergeSourceBuildLogs(
			sourceBuildLogEntries(
				0,
				Array.from({ length: 2001 }, (_, sequence) => `line-${sequence}`),
			),
		);

		expect(merged).toHaveLength(2000);
		expect(merged[0]?.sequence).toBe(1);
		expect(merged.at(-1)?.sequence).toBe(2000);
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

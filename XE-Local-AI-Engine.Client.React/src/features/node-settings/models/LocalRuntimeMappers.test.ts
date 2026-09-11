import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsModelFitV1LlamaCppRuntimeStatusResponse } from "@/core/api/generated";
import { toLlamaCppRuntimeStatus } from "@/features/node-settings/models/LocalRuntimeMappers";

describe("toLlamaCppRuntimeStatus", () => {
	it("maps a populated status response and surfaces the update-available flag", () => {
		const dto: XeLocalAiEngineClientEndpointsModelFitV1LlamaCppRuntimeStatusResponse = {
			installed: {
				tag: "b1000",
				variant: "cuda",
				asset: "asset.zip",
				installedAtUtc: 1700000000000,
				isSourceBuild: true,
				sourceRepository: "https://github.com/example/fork",
				sourceCommit: "a".repeat(40),
				sourceSelection: "custom",
				sourceRevisionMode: "explicitCommit",
				sourceRequestedCommit: "b".repeat(40),
			},
			recommendedTag: "b9692",
			upstreamLatestTag: "b9999",
			updateAvailable: true,
			isOffline: false,
			runningProcessCount: 0,
			isSourceBuild: true,
			rebuildAvailable: false,
		};

		const status = toLlamaCppRuntimeStatus(dto);

		expect(status.installed).toEqual({
			tag: "b1000",
			variant: "cuda",
			asset: "asset.zip",
			installedAtUtc: 1700000000000,
			isSourceBuild: true,
			sourceRepository: "https://github.com/example/fork",
			sourceCommit: "a".repeat(40),
			sourceSelection: "custom",
			sourceRevisionMode: "explicitCommit",
			sourceRequestedCommit: "b".repeat(40),
		});
		expect(status.recommendedTag).toBe("b9692");
		expect(status.upstreamLatestTag).toBe("b9999");
		expect(status.updateAvailable).toBe(true);
		expect(status.isOffline).toBe(false);
	});

	it("coalesces absent fields: null installed, no upstream, offline defaults", () => {
		const status = toLlamaCppRuntimeStatus({
			installed: null,
			recommendedTag: "b1000",
			updateAvailable: false,
			isOffline: false,
			runningProcessCount: 0,
			isSourceBuild: false,
			rebuildAvailable: false,
		});

		expect(status.installed).toBeNull();
		expect(status.upstreamLatestTag).toBeNull();
		expect(status.updateAvailable).toBe(false);
		expect(status.isOffline).toBe(false);
	});

	it("maps the runtime checked-at timestamp through to the domain status", () => {
		// The mapper projects field by field, so a DTO field it does not name is silently dropped — and the panel reads
		// the domain status, not the DTO.
		const status = toLlamaCppRuntimeStatus({
			installed: null,
			recommendedTag: "b1000",
			updateAvailable: false,
			isOffline: false,
			runningProcessCount: 0,
			isSourceBuild: false,
			rebuildAvailable: false,
			checkedAtUtc: 1700000000000,
		});

		expect(status.checkedAtUtc).toBe(1700000000000);
	});

	it("maps an absent checked-at timestamp to null", () => {
		const status = toLlamaCppRuntimeStatus({
			installed: null,
			recommendedTag: "b1000",
			updateAvailable: false,
			isOffline: false,
			runningProcessCount: 0,
			isSourceBuild: false,
			rebuildAvailable: false,
		});

		expect(status.checkedAtUtc).toBeNull();
	});
});

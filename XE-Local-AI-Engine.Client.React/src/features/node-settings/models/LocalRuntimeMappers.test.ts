import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsModelFitV1LlamaCppRuntimeStatusResponse } from "@/core/api/generated";
import { toLlamaCppRuntimeStatus } from "@/features/node-settings/models/LocalRuntimeMappers";

describe("toLlamaCppRuntimeStatus", () => {
	it("maps a populated status response and surfaces the update-available flag", () => {
		const dto: XeLocalAiEngineClientEndpointsModelFitV1LlamaCppRuntimeStatusResponse = {
			installed: { tag: "b1000", variant: "cuda", asset: "asset.zip", installedAtUtc: 1700000000000, isSourceBuild: false },
			recommendedTag: "b9692",
			upstreamLatestTag: "b9999",
			updateAvailable: true,
			isOffline: false,
			runningProcessCount: 0,
			isSourceBuild: false,
			rebuildAvailable: false,
		};

		const status = toLlamaCppRuntimeStatus(dto);

		expect(status.installed).toEqual({
			tag: "b1000",
			variant: "cuda",
			asset: "asset.zip",
			installedAtUtc: 1700000000000,
			isSourceBuild: false,
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
});

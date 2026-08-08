import { describe, expect, it } from "vitest";

import { toImageRuntimeStatus } from "@/features/node-settings/models/ImageRuntimeSourceBuildMappers";
import { canEjectImageRuntime, idleImageRuntimeActivity } from "@/features/node-settings/models/ImageRuntimeSourceBuildModels";

describe("image runtime source build models", () => {
	it("maps managed-runtime validity and activity without exposing undefined wire fields", () => {
		expect(
			toImageRuntimeStatus({
				managedRuntime: {
					validity: "invalid",
					desiredBackend: "cuda",
					sourceRepository: "https://github.com/example/stable-diffusion.cpp",
					sourceCommit: "a".repeat(40),
					sourceSelection: "custom",
					sourceRevisionMode: "explicitCommit",
					sourceRequestedCommit: "b".repeat(40),
					installedAtUtc: 123,
					invalidReason: "Smoke test failed.",
				},
				activity: {
					activeJobCount: 2,
					spawnReadinessCount: 1,
					residentProcessCount: 1,
					mutationReserved: false,
					evictionReserved: false,
					isBusy: true,
				},
			}),
		).toEqual({
			managedRuntime: {
				validity: "invalid",
				desiredBackend: "cuda",
				sourceRepository: "https://github.com/example/stable-diffusion.cpp",
				sourceCommit: "a".repeat(40),
				sourceSelection: "custom",
				sourceRevisionMode: "explicitCommit",
				sourceRequestedCommit: "b".repeat(40),
				installedAtUtc: 123,
				invalidReason: "Smoke test failed.",
			},
			activity: {
				activeJobCount: 2,
				spawnReadinessCount: 1,
				residentProcessCount: 1,
				mutationReserved: false,
				evictionReserved: false,
				isBusy: true,
			},
		});
	});

	it("allows ejection only for idle jobs with a resident process", () => {
		expect(canEjectImageRuntime({ ...idleImageRuntimeActivity, residentProcessCount: 1, isBusy: true })).toBe(true);
		expect(
			canEjectImageRuntime({
				...idleImageRuntimeActivity,
				activeJobCount: 1,
				residentProcessCount: 1,
				isBusy: true,
			}),
		).toBe(false);
		expect(canEjectImageRuntime(idleImageRuntimeActivity)).toBe(false);
	});
});

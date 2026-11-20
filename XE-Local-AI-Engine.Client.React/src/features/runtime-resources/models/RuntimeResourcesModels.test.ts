import { describe, expect, it } from "vitest";

import { toRuntimeResidents } from "@/features/runtime-resources/models/RuntimeResourcesModels";

describe("toRuntimeResidents", () => {
	// Zero is a real reading and must survive; only an absent or null value means "not known".
	it.each([
		["populated", { vramBytes: 1_600_000_000 }, 1_600_000_000],
		["zero", { vramBytes: 0 }, 0],
		["null", { vramBytes: null }, null],
		["omitted", {}, null],
	])("keeps a %s allocated VRAM value", (_case, vram, expected) => {
		const [resident] = toRuntimeResidents({
			items: [{ runtime: "modelLab", modelId: "Qwen/Qwen3-0.6B", state: "idle", canEject: true, ...vram }],
		});

		expect(resident).toEqual({
			runtime: "modelLab",
			modelId: "Qwen/Qwen3-0.6B",
			state: "idle",
			backend: null,
			canEject: true,
			vramBytes: expected,
		});
	});
});

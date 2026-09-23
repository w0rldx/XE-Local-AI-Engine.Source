import { describe, expect, it } from "vitest";

import { toLoadedModelsSnapshot } from "@/features/loaded-models/models/LoadedModelsMappers";

describe("toLoadedModelsSnapshot", () => {
	it("maps an available snapshot to the model names", () => {
		const snapshot = toLoadedModelsSnapshot({
			isAvailable: true,
			ollamaConfigured: true,
			error: null,
			items: [
				{ modelName: "llama3.1:8b", sizeBytes: 8_589_934_592, sizeVramBytes: 4_294_967_296, expiresAtUtc: 1_700_000_000_000 },
				{ modelName: "qwen2.5:3b", sizeBytes: 3_221_225_472, sizeVramBytes: null, expiresAtUtc: null },
			],
		});

		expect(snapshot).toEqual({
			isAvailable: true,
			ollamaConfigured: true,
			models: [{ modelName: "llama3.1:8b" }, { modelName: "qwen2.5:3b" }],
		});
	});

	it("surfaces ollamaConfigured:false so the query can stop polling an off runtime", () => {
		const snapshot = toLoadedModelsSnapshot({ isAvailable: false, ollamaConfigured: false, error: null, items: [] });

		expect(snapshot.ollamaConfigured).toBe(false);
	});
});

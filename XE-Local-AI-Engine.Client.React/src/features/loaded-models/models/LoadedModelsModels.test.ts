import { describe, expect, it } from "vitest";

import { toLoadedModelsSnapshot, toUnloadResult } from "@/features/loaded-models/models/LoadedModelsMappers";
import { loadedModelSchema } from "@/features/loaded-models/models/LoadedModelsModels";

describe("toLoadedModelsSnapshot", () => {
	it("maps an available snapshot with running models, coalescing optional fields", () => {
		const snapshot = toLoadedModelsSnapshot({
			isAvailable: true,
			ollamaConfigured: true,
			error: null,
			items: [
				{ modelName: "llama3.1:8b", sizeBytes: 8_589_934_592, sizeVramBytes: 4_294_967_296, expiresAtUtc: 1_700_000_000_000 },
				// VRAM omitted (CPU-only resident) — the mapper coalesces the missing field to null, not 0.
				{ modelName: "qwen2.5:3b", sizeBytes: 3_221_225_472, sizeVramBytes: null, expiresAtUtc: null },
			],
		});

		expect(snapshot.isAvailable).toBe(true);
		expect(snapshot.ollamaConfigured).toBe(true);
		expect(snapshot.error).toBeNull();
		expect(snapshot.models).toHaveLength(2);
		expect(snapshot.models[0]).toEqual({
			modelName: "llama3.1:8b",
			sizeBytes: 8_589_934_592,
			sizeVramBytes: 4_294_967_296,
			expiresAtUtc: 1_700_000_000_000,
		});
		expect(snapshot.models[1]?.sizeVramBytes).toBeNull();
		expect(snapshot.models[1]?.expiresAtUtc).toBeNull();
	});

	it("maps an unavailable snapshot to isAvailable:false with an empty model list and the sanitized error", () => {
		const snapshot = toLoadedModelsSnapshot({ isAvailable: false, ollamaConfigured: true, error: "Provider unreachable", items: [] });

		expect(snapshot.isAvailable).toBe(false);
		expect(snapshot.ollamaConfigured).toBe(true);
		expect(snapshot.error).toBe("Provider unreachable");
		expect(snapshot.models).toEqual([]);
	});

	it("surfaces ollamaConfigured:false so the page can stop polling an off runtime", () => {
		const snapshot = toLoadedModelsSnapshot({ isAvailable: false, ollamaConfigured: false, error: null, items: [] });

		expect(snapshot.ollamaConfigured).toBe(false);
	});

	it("defaults the optional error/model fields defensively when the wire omits them", () => {
		const snapshot = toLoadedModelsSnapshot({ isAvailable: false, ollamaConfigured: true, items: [] });

		expect(snapshot.isAvailable).toBe(false);
		expect(snapshot.error).toBeNull();
		expect(snapshot.models).toEqual([]);
	});

	it("coalesces a missing modelName to an empty string so the row stays renderable", () => {
		const snapshot = toLoadedModelsSnapshot({ isAvailable: true, ollamaConfigured: true, items: [{ modelName: "" }] });

		expect(snapshot.models[0]).toEqual({ modelName: "", sizeBytes: null, sizeVramBytes: null, expiresAtUtc: null });
	});
});

describe("toUnloadResult", () => {
	it("maps a successful unload, coalescing optional fields", () => {
		expect(toUnloadResult({ modelName: "llama3.1:8b", unloaded: true })).toEqual({
			modelName: "llama3.1:8b",
			unloaded: true,
		});
	});

	it("defaults unloaded to false and modelName to empty when omitted", () => {
		expect(toUnloadResult({ modelName: "", unloaded: false })).toEqual({ modelName: "", unloaded: false });
	});
});

describe("loadedModelSchema", () => {
	it("accepts a well-formed running-model shape", () => {
		const parsed = loadedModelSchema.safeParse({
			modelName: "llama3.1:8b",
			sizeBytes: 1024,
			sizeVramBytes: null,
			expiresAtUtc: 1_700_000_000_000,
		});

		expect(parsed.success).toBe(true);
	});

	it("rejects a non-string model name", () => {
		const parsed = loadedModelSchema.safeParse({ modelName: 42, sizeBytes: null, sizeVramBytes: null, expiresAtUtc: null });

		expect(parsed.success).toBe(false);
	});
});

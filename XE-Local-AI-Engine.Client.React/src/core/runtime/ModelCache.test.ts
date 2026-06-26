import { afterEach, describe, expect, it, vi } from "vitest";

import { type BlobStore, ModelCache, ModelHashMismatchError } from "./ModelCache";
import type { VoiceModelFile } from "./VoiceManifest";

interface MemoryStore extends BlobStore {
	readonly map: Map<string, ArrayBuffer>;
}

function createMemoryStore(): MemoryStore {
	const map = new Map<string, ArrayBuffer>();
	return {
		map,
		get: (key) => Promise.resolve(map.get(key)),
		put: (key, data) => {
			map.set(key, data);
			return Promise.resolve();
		},
		delete: (key) => {
			map.delete(key);
			return Promise.resolve();
		},
		keys: () => Promise.resolve([...map.keys()]),
	};
}

async function sha256Hex(bytes: Uint8Array): Promise<string> {
	const digest = await crypto.subtle.digest("SHA-256", bytes.buffer as ArrayBuffer);
	return [...new Uint8Array(digest)].map((byte) => byte.toString(16).padStart(2, "0")).join("");
}

function okResponse(bytes: Uint8Array): Response {
	return {
		ok: true,
		headers: { get: () => null },
		body: null,
		arrayBuffer: () => Promise.resolve(bytes.buffer),
	} as unknown as Response;
}

function modelFile(overrides: Partial<VoiceModelFile> & Pick<VoiceModelFile, "sha256">): VoiceModelFile {
	return {
		dtype: "q8",
		file: "model_quantized.onnx",
		byteSize: 0,
		downloadUrl: "https://models.test/model_quantized.onnx",
		...overrides,
	};
}

afterEach(() => {
	vi.unstubAllGlobals();
});

describe("ModelCache integrity + caching", () => {
	it("rejects a blob whose SHA-256 does not match the manifest, caching nothing", async () => {
		const bytes = new Uint8Array([1, 2, 3, 4]);
		vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(okResponse(bytes))));
		const store = createMemoryStore();
		const cache = new ModelCache(store);

		await expect(cache.getModelFile(modelFile({ sha256: "00".repeat(32) }), "model-x", "1")).rejects.toBeInstanceOf(
			ModelHashMismatchError,
		);
		expect([...store.map.keys()]).toEqual([]);
	});

	it("evicts the old-version blob after a verified re-download on a version bump", async () => {
		const store = createMemoryStore();
		store.map.set("model-x::1::model_quantized.onnx", new Uint8Array([9]).buffer);
		const newBytes = new Uint8Array([5, 6, 7]);
		const hash = await sha256Hex(newBytes);
		vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(okResponse(newBytes))));
		const cache = new ModelCache(store);

		await cache.getModelFile(modelFile({ sha256: hash }), "model-x", "2");

		expect(store.map.has("model-x::2::model_quantized.onnx")).toBe(true);
		expect(store.map.has("model-x::1::model_quantized.onnx")).toBe(false);
	});

	it("clears any partial and rethrows on a download failure (so the runtime can fall back)", async () => {
		vi.stubGlobal("fetch", vi.fn(() => Promise.reject(new Error("network down"))));
		const store = createMemoryStore();
		const cache = new ModelCache(store);

		await expect(cache.getModelFile(modelFile({ sha256: "ab".repeat(32) }), "model-x", "1")).rejects.toThrow(
			"network down",
		);
		expect([...store.map.keys()]).toEqual([]);
	});

	it("returns a cached current-version blob without fetching", async () => {
		const store = createMemoryStore();
		store.map.set("model-x::1::model_quantized.onnx", new Uint8Array([1, 1]).buffer);
		const fetchMock = vi.fn();
		vi.stubGlobal("fetch", fetchMock);
		const cache = new ModelCache(store);

		const result = await cache.getModelFile(modelFile({ sha256: "irrelevant" }), "model-x", "1");

		expect(new Uint8Array(result)).toEqual(new Uint8Array([1, 1]));
		expect(fetchMock).not.toHaveBeenCalled();
	});

	it("emits a progress event during download", async () => {
		const bytes = new Uint8Array([7, 7, 7]);
		const hash = await sha256Hex(bytes);
		vi.stubGlobal("fetch", vi.fn(() => Promise.resolve(okResponse(bytes))));
		const store = createMemoryStore();
		const cache = new ModelCache(store);
		const progress = vi.fn();
		cache.onProgress.on(progress);

		await cache.getModelFile(modelFile({ sha256: hash, byteSize: 3 }), "model-x", "1");

		expect(progress).toHaveBeenCalledWith(expect.objectContaining({ modelId: "model-x", loaded: 3, total: 3 }));
	});
});

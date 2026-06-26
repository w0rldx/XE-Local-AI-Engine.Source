import { describe, expect, it } from "vitest";

import type { XeLocalAiEngineClientEndpointsVoiceV1VoiceManifestResponse } from "@/core/api/generated";
import { defaultKokoroModelId } from "@/core/runtime/VoiceManifest";
import { adaptVoiceManifest } from "@/features/voice/VoiceManifestAdapter";

describe("adaptVoiceManifest", () => {
	it("maps a full optional response into the strict runtime manifest", () => {
		const response: XeLocalAiEngineClientEndpointsVoiceV1VoiceManifestResponse = {
			enabled: true,
			models: [
				{
					id: "model-a",
					version: "2.0",
					files: [{ dtype: "q8", file: "model_quantized.onnx", byteSize: 100, sha256: "ABCD", downloadUrl: "https://x/q8" }],
				},
			],
			voices: [{ id: "af_heart", name: "Heart", language: "en", gender: "female" }],
			defaultVoiceId: "af_heart",
		};

		const manifest = adaptVoiceManifest(response);

		expect(manifest.enabled).toBe(true);
		expect(manifest.models).toHaveLength(1);
		expect(manifest.models[0]).toEqual({
			id: "model-a",
			version: "2.0",
			// sha256 is lower-cased so it can be compared to the runtime's hex digest.
			files: [{ dtype: "q8", file: "model_quantized.onnx", byteSize: 100, sha256: "abcd", downloadUrl: "https://x/q8" }],
		});
		expect(manifest.voices[0]).toEqual({ id: "af_heart", name: "Heart", language: "en", gender: "female" });
		expect(manifest.defaultVoiceId).toBe("af_heart");
	});

	it("fills sane defaults for absent optionals", () => {
		const manifest = adaptVoiceManifest({
			// id/version/byteSize/sha256/downloadUrl all omitted — only the required-for-use dtype + file present.
			models: [{ files: [{ dtype: "fp32", file: "model.onnx" }] }],
			voices: [{ id: "v1" }],
		});

		expect(manifest.enabled).toBe(false);
		expect(manifest.models[0]?.id).toBe(defaultKokoroModelId);
		expect(manifest.models[0]?.version).toBe("1.0");
		expect(manifest.models[0]?.files[0]).toEqual({
			dtype: "fp32",
			file: "model.onnx",
			byteSize: 0,
			sha256: "",
			downloadUrl: "",
		});
		expect(manifest.voices[0]).toEqual({ id: "v1", name: "v1", language: "en", gender: "other" });
		// defaultVoiceId falls back to the first voice when omitted.
		expect(manifest.defaultVoiceId).toBe("v1");
	});

	it("treats undefined (unauthorized/missing) as a disabled manifest", () => {
		const manifest = adaptVoiceManifest(undefined);

		expect(manifest.enabled).toBe(false);
		expect(manifest.models).toEqual([]);
		expect(manifest.voices).toEqual([]);
		expect(manifest.defaultVoiceId).toBe("");
	});

	it("drops files with an unknown precision and voices with no id", () => {
		const manifest = adaptVoiceManifest({
			enabled: true,
			models: [
				{
					id: "m",
					files: [
						{ dtype: "bogus", file: "x.onnx" },
						{ dtype: "q8", file: "q.onnx" },
					],
				},
			],
			voices: [{ name: "no id" }, { id: "ok", name: "Ok" }],
		});

		expect(manifest.models[0]?.files).toHaveLength(1);
		expect(manifest.models[0]?.files[0]?.dtype).toBe("q8");
		expect(manifest.voices).toHaveLength(1);
		expect(manifest.voices[0]?.id).toBe("ok");
	});
});

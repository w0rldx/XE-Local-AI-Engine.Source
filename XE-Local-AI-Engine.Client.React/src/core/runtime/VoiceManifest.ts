// Voice manifest contract — the client-side shape the runtime consumes.
//
// LANE SEAM (Lane B ↔ Lane C): the backend (Lane A) exposes `GET /api/local/v1/voice/manifest` returning a
// `VoiceManifestResponse`. Lane C wires the typed hey-api query and adapts the generated response into this
// `VoiceManifest` shape, then injects it into `VoiceRuntime`. Lane B is independent of the backend by depending only
// on this interface plus the `mockVoiceManifest` below. To swap in the real manifest, Lane C builds a
// `VoiceManifest` from `getVoiceManifestOptions()` data and passes it where the mock is used today — no Lane B change.
//
// The wire contract (Lane A, plan §7.1) is: `Enabled`, `Models[{ id, version, files[{ dtype, file, byteSize, sha256,
// downloadUrl }] }]`, `Voices[{ id, name, language, gender }]`, `DefaultVoiceId`. These TS interfaces mirror that
// 1:1 in idiomatic camelCase (matches the repo's other generated-DTO adapters).

/** Kokoro / onnxruntime weight precisions. fp32 → WebGPU, q8 → WASM (plan §7.0). */
export type VoiceModelDtype = "fp32" | "fp16" | "q8" | "q4" | "q4f16";

/** IETF short language code tagging both content language and voice language (e.g. "en", "de"). */
export type VoiceLanguageCode = string;

/** Speaker gender as advertised by the catalog; "other" covers unspecified. */
export type VoiceGender = "male" | "female" | "other";

/** One downloadable weight file for a model at a given precision. `sha256` gates integrity before caching (§3/§7.1). */
export interface VoiceModelFile {
	readonly dtype: VoiceModelDtype;
	/** On-disk ONNX filename, e.g. "model.onnx" (fp32) or "model_quantized.onnx" (q8) — used as part of the cache key. */
	readonly file: string;
	readonly byteSize: number;
	/** Lowercase hex SHA-256 of the file contents; the manifest is the source of truth (Lane A computed it). */
	readonly sha256: string;
	readonly downloadUrl: string;
}

/** A model the client is allowed to load. `version` keys the cache so a bump evicts + re-downloads (§7.2). */
export interface VoiceModelDescriptor {
	readonly id: string;
	readonly version: string;
	readonly files: readonly VoiceModelFile[];
}

/** A selectable voice profile; `language` drives the en→Kokoro / non-en→Web Speech routing (§3.7 / D2). */
export interface VoiceProfile {
	readonly id: string;
	readonly name: string;
	readonly language: VoiceLanguageCode;
	readonly gender: VoiceGender;
}

/** The full client-side manifest. `enabled` is the operator-owned node-level gate (plan §7.1). */
export interface VoiceManifest {
	readonly enabled: boolean;
	readonly models: readonly VoiceModelDescriptor[];
	readonly voices: readonly VoiceProfile[];
	readonly defaultVoiceId: string;
}

/** The default Kokoro model id (plan §6.1). */
export const defaultKokoroModelId = "onnx-community/Kokoro-82M-v1.0-ONNX";

// Mock manifest so Lane B is testable + buildable before Lane A lands. The sha256 values are placeholders — Lane C
// replaces this whole object with the backend-supplied manifest. English Kokoro voices + a German Web-Speech-routed
// voice illustrate the bilingual routing seam (Kokoro has no German voice, so "de" content routes to Web Speech).
export const mockVoiceManifest: VoiceManifest = {
	enabled: true,
	models: [
		{
			id: defaultKokoroModelId,
			version: "1.0",
			files: [
				{
					dtype: "fp32",
					file: "model.onnx",
					byteSize: 326_000_000,
					sha256: "0000000000000000000000000000000000000000000000000000000000000000",
					downloadUrl: `https://huggingface.co/${defaultKokoroModelId}/resolve/main/onnx/model.onnx`,
				},
				{
					dtype: "q8",
					file: "model_quantized.onnx",
					byteSize: 92_400_000,
					sha256: "1111111111111111111111111111111111111111111111111111111111111111",
					downloadUrl: `https://huggingface.co/${defaultKokoroModelId}/resolve/main/onnx/model_quantized.onnx`,
				},
			],
		},
	],
	voices: [
		{ id: "af_heart", name: "Heart", language: "en", gender: "female" },
		{ id: "am_michael", name: "Michael", language: "en", gender: "male" },
		// Web-Speech-served German voice (resolved from the OS voice list at runtime, no Kokoro weights).
		{ id: "de_web_default", name: "System (Deutsch)", language: "de", gender: "other" },
	],
	defaultVoiceId: "af_heart",
};

/** Returns the first allowed model whose files include the requested precision, or undefined if none. */
export function findAllowedModel(
	manifest: VoiceManifest,
	dtype: VoiceModelDtype,
): VoiceModelDescriptor | undefined {
	return manifest.models.find((model) => model.files.some((file) => file.dtype === dtype));
}

// Looks up a selectable voice by id (undefined when no id or no catalog match). The "selected voice always wins"
// routing (D2) uses this to drive the engine/ladder from the chosen voice's OWN language — so chat matches the
// node-settings preview exactly — instead of guessing the language from the answer text.
export function findVoiceById(
	manifest: VoiceManifest,
	voiceId: string | undefined,
): VoiceProfile | undefined {
	if (!voiceId) {
		return undefined;
	}

	return manifest.voices.find((voice) => voice.id === voiceId);
}

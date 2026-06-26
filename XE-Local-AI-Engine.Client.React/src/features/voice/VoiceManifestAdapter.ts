// Adapts the generated (all-optional, server-shaped) voice manifest response into Lane B's strict `VoiceManifest`
// shape, filling sane defaults for every absent optional (plan §7.2 / VoiceManifest LANE SEAM). The generated DTO
// types every field optional and the wire is camelCase already, so the adapter's job is null-coalescing + dropping
// entries that are unusable (a file with an unknown dtype, a voice with no id) rather than renaming.

import type { XeLocalAiEngineClientEndpointsVoiceV1VoiceManifestResponse } from "@/core/api/generated";
import {
	defaultKokoroModelId,
	type VoiceGender,
	type VoiceManifest,
	type VoiceModelDtype,
	type VoiceModelFile,
	type VoiceProfile,
} from "@/core/runtime/VoiceManifest";

const KNOWN_DTYPES: readonly VoiceModelDtype[] = ["fp32", "fp16", "q8", "q4", "q4f16"];
const KNOWN_GENDERS: readonly VoiceGender[] = ["male", "female", "other"];

function toDtype(value: string | undefined): VoiceModelDtype | undefined {
	return KNOWN_DTYPES.find((dtype) => dtype === value);
}

function toGender(value: string | undefined): VoiceGender {
	return KNOWN_GENDERS.find((gender) => gender === value) ?? "other";
}

function adaptFile(
	file: NonNullable<NonNullable<XeLocalAiEngineClientEndpointsVoiceV1VoiceManifestResponse["models"]>[number]["files"]>[number],
): VoiceModelFile | undefined {
	const dtype = toDtype(file.dtype);
	// A file whose precision the runtime cannot map (findAllowedModel keys on dtype) is unusable — drop it.
	if (!dtype || !file.file) {
		return undefined;
	}

	return {
		dtype,
		file: file.file,
		byteSize: file.byteSize ?? 0,
		sha256: (file.sha256 ?? "").toLowerCase(),
		downloadUrl: file.downloadUrl ?? "",
	};
}

function isVoiceProfile(voice: VoiceProfile | undefined): voice is VoiceProfile {
	return voice !== undefined;
}

/**
 * Maps the generated manifest response to the runtime `VoiceManifest`. Absent optionals collapse to defaults; files
 * with an unknown precision and voices with no id are dropped (they cannot be selected by the runtime). When the
 * server omits `enabled` the feature reads as OFF — the operator gate stays closed unless explicitly opened.
 */
export function adaptVoiceManifest(
	response: XeLocalAiEngineClientEndpointsVoiceV1VoiceManifestResponse | undefined,
): VoiceManifest {
	const models = (response?.models ?? []).map((model) => ({
		id: model.id ?? defaultKokoroModelId,
		version: model.version ?? "1.0",
		files: (model.files ?? []).map(adaptFile).filter((file): file is VoiceModelFile => file !== undefined),
	}));

	const voices = (response?.voices ?? [])
		.map((voice): VoiceProfile | undefined =>
			voice.id
				? { id: voice.id, name: voice.name ?? voice.id, language: voice.language ?? "en", gender: toGender(voice.gender) }
				: undefined,
		)
		.filter(isVoiceProfile);

	return {
		enabled: response?.enabled ?? false,
		models,
		voices,
		defaultVoiceId: response?.defaultVoiceId ?? voices[0]?.id ?? "",
	};
}

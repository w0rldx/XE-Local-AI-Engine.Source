import {
	type ImageGenerationFormValues,
	type ImageModelView,
	imageFormDefaultsForModel,
	imageGenerationFormSchema,
} from "@/features/images/models/ImageModels";

// The operator's own steps / CFG / sampler for one installed model, remembered in this browser. The family defaults
// are a starting point, not always the right answer: a manually imported original Qwen-Image wants CFG 2.5 where the
// 2.1 family default is 6.0, and re-typing that after every model switch or reload is how it gets forgotten.
// A per-viewer convenience, so localStorage; every access is guarded and a failure reads as "no override".
export const imageFormOverridesKeyPrefix = "xe.images.formOverrides.";

const overridesSchema = imageGenerationFormSchema.pick({ steps: true, cfgScale: true, sampler: true });
type ImageFormOverrides = Pick<ImageGenerationFormValues, "steps" | "cfgScale" | "sampler">;

export function readImageFormOverrides(modelId: string | undefined): ImageFormOverrides | null {
	if (!modelId) {
		return null;
	}
	try {
		const raw = globalThis.localStorage?.getItem(imageFormOverridesKeyPrefix + modelId);
		if (!raw) {
			return null;
		}
		const parsed = overridesSchema.safeParse(JSON.parse(raw));
		return parsed.success ? parsed.data : null;
	} catch {
		return null;
	}
}

// An out-of-range value (a half-typed NumberInput) is not stored, so the last valid override survives it.
export function writeImageFormOverrides(modelId: string, overrides: ImageFormOverrides): void {
	const parsed = overridesSchema.safeParse(overrides);
	if (!(modelId && parsed.success)) {
		return;
	}
	try {
		globalThis.localStorage?.setItem(imageFormOverridesKeyPrefix + modelId, JSON.stringify(parsed.data));
	} catch {
		// Unavailable storage or quota: the form state still updates, it just is not remembered.
	}
}

export function clearImageFormOverrides(modelId: string): void {
	try {
		globalThis.localStorage?.removeItem(imageFormOverridesKeyPrefix + modelId);
	} catch {
		// Nothing stored that could be read back either.
	}
}

/** The family defaults for a model with the operator's remembered override, if any, applied over them. */
export function imageFormValuesForModel(model: ImageModelView | undefined): ImageGenerationFormValues {
	return { ...imageFormDefaultsForModel(model), ...readImageFormOverrides(model?.modelName) };
}

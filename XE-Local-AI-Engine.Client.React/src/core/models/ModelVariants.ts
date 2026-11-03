import { LLAMACPP_PROVIDER } from "@/core/models/LocalModelProviders";

// An installed GGUF model is named `{repo}:{quant}`, so the part before the last colon is its repo; null without a colon.
export function modelRepoId(modelName: string): string | null {
	const colon = modelName.lastIndexOf(":");
	return colon < 0 ? null : modelName.slice(0, colon).toLowerCase();
}

// What tells a repo's variants apart: the quant after the colon, or the whole name without one.
export function modelVariantLabel(modelName: string): string {
	return modelName.slice(modelName.lastIndexOf(":") + 1);
}

/** The other installed llama.cpp models of `modelName`'s repo: the files the Inspect panel can compare it with. */
export function sameRepoVariants(
	modelName: string,
	models: readonly { modelName: string; provider?: string | null }[],
): string[] {
	const repo = modelRepoId(modelName);
	return repo === null
		? []
		: models
				.filter(
					(model) =>
						model.provider === LLAMACPP_PROVIDER && model.modelName !== modelName && modelRepoId(model.modelName) === repo,
				)
				.map((model) => model.modelName);
}

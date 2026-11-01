// Pure display view-models + formatting helpers for the models feature. The wire DTO types live in the generated
// hey-api SDK (the migrated page reads them directly); the mappers that coalesce the optional-field generated
// responses into these view-models live in LocalModelMappers.ts.

import type { XeLocalAiEngineProvidersAbstractionsContractsLocalModelOrigin } from "@/core/api/generated";

export interface LocalModelViewModel {
	modelName: string;
	// The runtime that serves this model ("llamacpp", "Ollama", "CodexOAuth", "AzureFoundry"). Only "llamacpp" models
	// honor a per-model launch-argument override, so the Advanced tab is gated on this.
	provider: string;
	origin: LocalModelOrigin | null;
	sizeLabel: string;
	modifiedDateLabel: string;
	familyLabel: string;
	parameterSizeLabel: string;
	quantizationLabel: string;
	isSelected: boolean;
	kind: string;
	detectedKind: string;
	capabilities: string[];
	isOverridden: boolean;
}

export type LocalModelOrigin = XeLocalAiEngineProvidersAbstractionsContractsLocalModelOrigin;

export const emptyModelValue = "—";

// An installed GGUF model is named `{repo}:{quant}`, so the part before the last colon is its repo; null without a colon.
export function modelRepoId(modelName: string): string | null {
	const colon = modelName.lastIndexOf(":");
	return colon < 0 ? null : modelName.slice(0, colon).toLowerCase();
}

// What tells a repo's variants apart: the quant after the colon, or the whole name without one.
export function modelVariantLabel(modelName: string): string {
	return modelName.slice(modelName.lastIndexOf(":") + 1);
}

/** The other installed llama.cpp models of `modelName`'s repo: the files the Inspect tab can compare it with. */
export function sameRepoVariants(
	modelName: string,
	models: readonly Pick<LocalModelViewModel, "modelName" | "provider">[],
): string[] {
	const repo = modelRepoId(modelName);
	return repo === null
		? []
		: models
				.filter(
					(model) => model.provider === "llamacpp" && model.modelName !== modelName && modelRepoId(model.modelName) === repo,
				)
				.map((model) => model.modelName);
}

export function formatModelSize(sizeBytes: number | null | undefined): string {
	if (sizeBytes === null || sizeBytes === undefined || !Number.isFinite(sizeBytes) || sizeBytes < 0) {
		return emptyModelValue;
	}

	if (sizeBytes >= 1_073_741_824) {
		return `${(sizeBytes / 1_073_741_824).toFixed(1)} GB`;
	}

	if (sizeBytes >= 1_048_576) {
		return `${(sizeBytes / 1_048_576).toFixed(1)} MB`;
	}

	return `${(sizeBytes / 1024).toFixed(1)} KB`;
}

export function formatModelModifiedDate(modifiedAtUtc: number | null | undefined): string {
	if (modifiedAtUtc === null || modifiedAtUtc === undefined || !Number.isFinite(modifiedAtUtc)) {
		return emptyModelValue;
	}

	return new Date(modifiedAtUtc).toISOString().slice(0, 10);
}

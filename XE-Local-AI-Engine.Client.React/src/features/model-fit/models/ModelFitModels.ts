import { z } from "zod";

// The six llmfit-supported use cases (server-enforced allowlist, Marker 0). The recommendations page lets the
// operator pick one and re-queries; the backend validates the value again and is authoritative. Stored as a
// string literal union so the selector and query key stay type-safe.
export type ModelFitUseCase = "general" | "coding" | "reasoning" | "chat" | "multimodal" | "embedding";

export const modelFitUseCases: readonly ModelFitUseCase[] = ["general", "coding", "reasoning", "chat", "multimodal", "embedding"];

// Default use case the page opens on (matches the scheduler template default). The backend defaults providerName
// to "ollama"; the page only ever queries the single supported provider, so it is a constant here.
export const defaultModelFitUseCase: ModelFitUseCase = "coding";
export const defaultModelFitProviderName = "ollama";

// Breadth (--limit) the manual "Refresh now" sends per run. Set high enough to fetch the whole use-case catalog
// (llmfit caps at the catalog size — ~166 for coding — regardless of a larger value), so the table can show the full
// selection with client-side pagination. Validated server-side against the 1..500 allowlist (Lane H1).
export const recommendationRefreshLimit = 500;

// llmfit utility-image purposes (a [Flags] enum on the wire, projected to a string array). An image may serve
// recommendation, benchmark, or both. Surfaced as purpose badges on the read-only approved-images page.
export type ModelFitImagePurpose = "ModelRecommendation" | "ModelBenchmark";

// Domain view-model for one approved llmfit utility image. Read-only on the wire: imageReference is pinned by
// code-seed/migration and never editable from the browser, so the model carries it purely for display. All
// optional metadata fields are nullable (the wire omits them; the mapper coalesces to null). Timestamps are
// epoch milliseconds.
export interface ApprovedImage {
	readonly approvedImageId: string;
	readonly displayName: string;
	readonly description: string | null;
	readonly purpose: readonly ModelFitImagePurpose[];
	readonly imageReference: string;
	readonly sourceUrl: string | null;
	readonly upstreamVersion: string | null;
	readonly enabled: boolean;
	readonly deprecatedAtUtc: number | null;
	readonly replacementApprovedImageId: string | null;
	readonly lastUsedAtUtc: number | null;
	readonly lastSuccessfulRunAtUtc: number | null;
	readonly diagnostics: string | null;
}

// Domain view-model for one ranked recommendation row. Sanitized projection of the normalized
// model_fit_recommendations row — it never carries raw llmfit JSON. Numeric fit metrics are nullable because
// llmfit may omit them (e.g. no separate VRAM figure). isInstalled / pullModelName drive the install indicator.
export interface ModelFitRecommendation {
	readonly rank: number;
	readonly modelName: string;
	readonly providerModelName: string | null;
	readonly score: number;
	readonly fitLevel: string | null;
	readonly runMode: string | null;
	readonly quantization: string | null;
	readonly estimatedTokensPerSecond: number | null;
	readonly requiredRamMb: number | null;
	readonly requiredVramMb: number | null;
	readonly contextTokens: number | null;
	readonly isInstalled: boolean;
	readonly pullModelName: string | null;
	// The model's release date (ISO date string) when llmfit reports one; null otherwise. A "newer model" signal.
	readonly releaseDate: string | null;
}

// Domain view-model for the latest cached recommendation snapshot. hasCache:false is the explicit empty /
// diagnostics state — when it is false every snapshot field is null and recommendations is empty (the backend
// returns 200 on a cache miss, not a 404). Reads never run llmfit; the cache is refreshed only via the scheduler.
export interface ModelFitLatestRecommendations {
	readonly hasCache: boolean;
	readonly snapshotId: string | null;
	readonly status: string | null;
	readonly sourceImageId: string | null;
	readonly useCase: ModelFitUseCase | string | null;
	readonly providerName: string | null;
	readonly lastRefreshedAtUtc: number | null;
	readonly recommendations: readonly ModelFitRecommendation[];
}

// Filters for the latest-recommendations query. providerName is optional (server defaults to "ollama").
export interface ModelFitRecommendationFilters {
	readonly useCase: ModelFitUseCase;
	readonly providerName?: string;
}

const modelFitUseCaseSchema = z.enum(["general", "coding", "reasoning", "chat", "multimodal", "embedding"]);

// Reserved scheduler template id the refresh-now action fires. The refresh endpoint triggers an EXISTING
// model-recommendation-check job (it does not create one), so the page filters the scheduler job list by this
// template id to decide whether "Refresh now" is enabled.
export const modelRecommendationCheckTemplateId = "model-recommendation-check";

export { modelFitUseCaseSchema };

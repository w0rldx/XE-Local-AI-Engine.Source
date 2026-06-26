import { z } from "zod";

// The six llmfit-supported use cases (server-enforced allowlist). The recommendations page lets the
// operator pick one and re-queries; the backend validates the value again and is authoritative. Stored as a
// string literal union so the selector and query key stay type-safe.
export type ModelFitUseCase = "general" | "coding" | "reasoning" | "chat" | "multimodal" | "embedding";

export const modelFitUseCases: readonly ModelFitUseCase[] = ["general", "coding", "reasoning", "chat", "multimodal", "embedding"];

// Default use case the page opens on (matches the scheduler template default).
export const defaultModelFitUseCase: ModelFitUseCase = "coding";

// Breadth (--limit) the manual "Refresh now" sends per run. Bounded to the backend's authoritative 1..50 ceiling
// (ModelFitRequestValidator.MaxLimit, mirrored by the endpoint + handler JSON schema); sending more 400s with
// "Limit is out of range." The advisor caps at the candidate-list size anyway, so 50 covers a node's use-case
// catalog, and the table paginates client-side.
export const recommendationRefreshLimit = 50;

// Default quant the advisor recommends when the operator does not override it (HF policy, Q4_K_M). Surfaced here so
// the GGUF download flow can default the requested quant without re-deriving it from a recommendation row.
export const defaultGgufQuant = "Q4_K_M";

// Domain view-model for one ranked recommendation row. Sanitized projection of the normalized
// model_fit_recommendations row — it never carries raw advisor JSON. Numeric fit metrics are nullable because the
// advisor may omit them (e.g. no separate VRAM figure when running CPU-only). The advisor now fills file + quant +
// the memory-fit estimate (requiredRamMb / requiredVramMb / contextTokens), and runMode carries the CPU/GPU mode.
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
	// The model's release date (ISO date string) when the advisor reports one; null otherwise. A "newer model" signal.
	readonly releaseDate: string | null;
	// True when the model's publisher is a known reputable GGUF packager / first-party org; false for an unknown or
	// community publisher. Never a filter — untrusted rows still render, but the table flags them with a warning badge.
	readonly isTrustedPublisher: boolean;
}

// Domain view-model for the latest cached recommendation snapshot. hasCache:false is the explicit empty /
// diagnostics state — when it is false every snapshot field is null and recommendations is empty (the backend
// returns 200 on a cache miss, not a 404). Reads never run the advisor; the cache is refreshed only via the scheduler.
export interface ModelFitLatestRecommendations {
	readonly hasCache: boolean;
	readonly snapshotId: string | null;
	readonly status: string | null;
	readonly useCase: ModelFitUseCase | string | null;
	readonly lastRefreshedAtUtc: number | null;
	readonly recommendations: readonly ModelFitRecommendation[];
}

// Filters for the latest-recommendations query. Only the use case scopes the cache read (the advisor no longer
// carries a provider param — local llama.cpp is the only target).
export interface ModelFitRecommendationFilters {
	readonly useCase: ModelFitUseCase;
}

const modelFitUseCaseSchema = z.enum(["general", "coding", "reasoning", "chat", "multimodal", "embedding"]);

// Known GPU-vendor values the hardware profile reports. Anything outside this set (a future vendor) maps to "unknown".
export type HardwareGpuVendor = "nvidia" | "amd" | "intel" | "none" | "unknown";

export const hardwareGpuVendors: readonly HardwareGpuVendor[] = ["nvidia", "amd", "intel", "none", "unknown"];

// Domain view-model for the node hardware profile. Carries only aggregate RAM/VRAM/vendor/CPU/disk figures (the
// backend redacts machine identifiers). vramKnown is the explicit "VRAM undetectable" flag — when false the advisor
// degrades to a CPU/RAM-only fit budget and the profile card shows "VRAM unknown". gpuAccelAvailable false ⇒ CPU mode.
export interface HardwareProfile {
	readonly totalRamBytes: number;
	readonly availableRamBytes: number;
	readonly vramBytes: number | null;
	readonly vramKnown: boolean;
	readonly gpuVendor: HardwareGpuVendor;
	readonly gpuAccelAvailable: boolean;
	readonly cpuCores: number;
	readonly freeDiskBytes: number;
}

// Reserved scheduler template id the refresh-now action fires. The refresh endpoint triggers an EXISTING
// model-recommendation-check job (it does not create one), so the page filters the scheduler job list by this
// template id to decide whether "Refresh now" is enabled.
export const modelRecommendationCheckTemplateId = "model-recommendation-check";

export { modelFitUseCaseSchema };

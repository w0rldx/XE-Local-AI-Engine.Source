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

// The three sections the backend now splits a use-case's ranked recommendations into: hardware-confident picks,
// reduced-quality-but-runnable picks, and trending catalog entries to explore. Stored as a string literal union
// (mirrors the modelFitUseCases pattern) so the page's section split and the i18n key lookup stay type-safe.
export type ModelFitRecommendationSection = "recommended" | "canRun" | "explore";

export const modelFitRecommendationSections: readonly ModelFitRecommendationSection[] = ["recommended", "canRun", "explore"];

// The curated-catalog quality tier the advisor attaches to a row, when the row is backed by a catalog entry
// (S = best-in-class, A = strong, B = solid). Null for rows the advisor ranked without a catalog match.
export type ModelFitCatalogTier = "S" | "A" | "B" | null;

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
	// Which of the three ranked groups this row belongs to (recommended for this hardware / can run at reduced
	// quality / explore-trending). The page uses this to split one flat list into three grouped tables.
	readonly section: ModelFitRecommendationSection;
	// Curated-catalog quality tier (S/A/B), or null when the row has no catalog match.
	readonly tier: ModelFitCatalogTier;
	// Stable curated-catalog entry id backing this row, or null when the row has no catalog match.
	readonly catalogId: string | null;
	// Human-friendly catalog display name (e.g. "Qwen2.5 Coder 32B"), preferred over the raw modelName when present.
	readonly catalogDisplayName: string | null;
	// Short curated-catalog editorial note about the model, or null when there is none.
	readonly catalogNotes: string | null;
	// True when the advisor's fit estimate offloads some Mixture-of-Experts layers to CPU/RAM (slower, higher
	// quality than a smaller quant) rather than running the whole model on GPU.
	readonly expertsOffloaded: boolean;
	// GPU memory (GB) portion of an experts-offloaded fit split; null unless expertsOffloaded is true and the
	// advisor reported a split.
	readonly gpuGb: number | null;
	// CPU/RAM memory (GB) portion of an experts-offloaded fit split; null unless expertsOffloaded is true and the
	// advisor reported a split.
	readonly cpuGb: number | null;
	// ADVISORY-ONLY quantized-KV-cache estimate (catalog rows with complete GGUF metadata): the KV quant label the
	// advisory was computed at (currently "Q8_0"), or null when no advisory exists. The row's fit/required-memory
	// figures are ALWAYS the fp16-KV estimate — this only hints at the headroom a quantized KV cache could unlock.
	readonly kvQuant: string | null;
	// Estimated total footprint (GB) with the quantized KV cache; null when kvQuant is null.
	readonly kvQuantEstimatedGb: number | null;
	// Scored-budget headroom (GB) with the quantized KV cache (negative = still would not fit); null when kvQuant is null.
	readonly kvQuantHeadroomGb: number | null;
	// Whether the model would fit its scored budget with the quantized KV cache; null when kvQuant is null.
	readonly kvQuantFits: boolean | null;
	// Always true when an advisory is present — a quantized KV cache requires flash attention; null when kvQuant is null.
	readonly kvQuantRequiresFlashAttention: boolean | null;
	// KV-cache bytes for ONE token of context at the snapshot's context target, computed at kvBytesPerTokenQuant (the
	// chat launch's element size, not the fp16 estimate the required-memory columns use). Null when the GGUF header
	// cannot size the KV term or the row predates this field. Never render it without the quant label.
	readonly kvBytesPerToken: number | null;
	// The KV element size kvBytesPerToken was computed at (currently "Q8_0"); null when kvBytesPerToken is null.
	readonly kvBytesPerTokenQuant: string | null;
	// The model's attention shape as a stable lowercase token ("mla" | "swa" | "gqa" | "mha"), derived from GGUF
	// numbers rather than the architecture string. Null on a row that predates this field.
	readonly attentionArch: string | null;
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
	// Runtime device audit: the SELECTED inference backend, whether a GPU was expected for it, and whether it
	// silently fell back to the CPU (a GPU box whose runtime cannot use the GPU). When cpuFallback is true the reason +
	// remediation carry the actionable "why + what to do" the CPU-fallback alert surfaces.
	readonly inferenceBackend: string;
	readonly gpuExpected: boolean;
	readonly cpuFallback: boolean;
	readonly cpuFallbackReason: string | null;
	readonly cpuFallbackRemediation: string | null;
	// Set only when inferenceBackend is "unknown" because the device probe could not complete. Distinct from
	// cpuFallback: nobody proved the GPU is unused, but nobody proved it is used either, and model sizing on this page
	// still assumes the VRAM is usable. Without it a wedged driver looks exactly like a healthy machine.
	readonly backendUndeterminedReason: string | null;
	// Measured GPU layer placement from the most recent observed model load, read from llama.cpp's own load banner.
	// Null until a model has been loaded and observed. offloaded < total means the GPU IS in use but part of the model
	// runs from system RAM (much slower) — a distinct state from cpuFallback, never folded into it. The model name and
	// role are carried so the figures are never attributed to the wrong model on a multi-model node.
	readonly gpuOffloadedLayers: number | null;
	readonly gpuTotalLayers: number | null;
	readonly gpuOffloadModelName: string | null;
	readonly gpuOffloadRole: string | null;
}

// Reserved scheduler template id the refresh-now action fires. The refresh endpoint triggers an EXISTING
// model-recommendation-check job (it does not create one), so the page filters the scheduler job list by this
// template id to decide whether "Refresh now" is enabled.
export const modelRecommendationCheckTemplateId = "model-recommendation-check";

// Known catalog-source values the backend reports for the curated model catalog snapshot the advisor is ranking
// against: "bundled" ships with the app, "remote" was fetched live, "remoteLastGood" is a stale cached fetch served
// because the live fetch failed. Anything outside this set (a future source) maps to "bundled" (the safe baseline).
export type ModelFitCatalogSource = "bundled" | "remote" | "remoteLastGood";

export const modelFitCatalogSources: readonly ModelFitCatalogSource[] = ["bundled", "remote", "remoteLastGood"];

// Domain view-model for the curated model catalog metadata (version, source, freshness). Read-only; the operator can
// trigger a refresh but the catalog itself is not editable from this page.
export interface ModelFitCatalogInfo {
	readonly catalogVersion: string;
	readonly updatedAt: string | null;
	readonly source: ModelFitCatalogSource;
	readonly fetchedAtUtc: number | null;
	readonly sourceUrl: string | null;
	readonly modelCount: number;
	// False when the node has no configured live-refresh source (ModelCatalogOptions.RefreshUrl is unset), so
	// "Refresh catalog" cannot fetch anything new and the bundled snapshot is authoritative. The UI must not report a
	// refresh as successful when this is false.
	readonly refreshSourceConfigured: boolean;
}

export { modelFitUseCaseSchema };

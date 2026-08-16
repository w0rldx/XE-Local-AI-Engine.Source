import type {
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRunDetailResponse as RunDetailResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1BenchmarkRunSummaryResponse as RunSummaryResponse,
	XeLocalAiEngineClientEndpointsBenchmarksV1StartBenchmarkRunRequest as StartRunRequest,
} from "@/core/api/generated";
import type {
	BenchmarkFlashAttentionMode,
	BenchmarkKvCacheType,
	BenchmarkKvCacheTypeSource,
} from "@/features/benchmarks/models/BenchmarkModels";

// TEMPORARY SWAP SEAM. The backend already answers with the launch-evidence contract (plan A, slice 2) but the hey-api
// client has not been regenerated yet, so `@/core/api/generated` does not carry those members. Every hand-written wire
// shape lives in THIS file and nowhere else; `BenchmarkMappers.ts` is its only consumer. After `pnpm run openapi`
// (desktop-mode recipe) this file is deleted and the mapper imports the generated names instead — the view models in
// `BenchmarkModels.ts` and every component stay untouched.

/** Flat launch columns the run summary/detail responses carry per side, `primary…` and `judge…`. */
type LaunchColumns<TPrefix extends string> = {
	[K in `${TPrefix}${
		| "Variant"
		| "KvCacheType"
		| "KvAutoReason"
		| "IntendedLaunchIdentity"
		| "IntendedExecutableSha256"
		| "EffectiveLaunchIdentity"
		| "EffectiveBackend"
		| "ExecutableSha256"
		| "ReceiptHash"
		| "EnvironmentFactsHash"}`]?: string | null;
} & {
	[K in `${TPrefix}${"PlacementOffloaded" | "PlacementTotal"}`]?: number | null;
} & {
	[K in `${TPrefix}KvCacheTypeSource`]?: BenchmarkKvCacheTypeSource | null;
} & {
	[K in `${TPrefix}FlashAttentionMode`]?: BenchmarkFlashAttentionMode | null;
} & {
	[K in `${TPrefix}HasAuxAssets`]?: boolean | null;
};

/** Decoded provider receipt (`LlamaServerLaunchReceipt` v1). Rendered as opaque facts, so values stay permissive. */
export interface BenchmarkLaunchReceiptWire {
	receiptVersion?: number | null;
	variant?: string | null;
	os?: string | null;
	executableVersion?: string | null;
	executableSha256?: string | null;
	manifestSha256?: string | null;
	launchProjection?: {
		autoFit?: boolean | null;
		metrics?: boolean | null;
		contextTokens?: number | null;
		gpuLayers?: number | null;
		tensorSplit?: string | null;
		overrideTensor?: string | null;
		kvCacheTypeK?: string | null;
		kvCacheTypeV?: string | null;
		flashAttentionMode?: string | null;
		threads?: number | null;
		threadsBatch?: number | null;
		batchSize?: number | null;
		ubatchSize?: number | null;
		parallel?: number | null;
		cacheReuse?: number | null;
		cacheRamMiB?: number | null;
		jinja?: boolean | null;
		pooling?: string | null;
	} | null;
	auxAssets?: { hasLora?: boolean | null; hasMmproj?: boolean | null; hasDraft?: boolean | null } | null;
	placement?: { outcome?: string | null; offloadedLayers?: number | null; totalLayers?: number | null } | null;
	effectiveContextTokens?: number | null;
	benchmarkLaunchPolicy?: {
		version?: number | null;
		chatCacheReuse?: number | null;
		chatCacheRamMiB?: number | null;
		speculativeDecodingEnabled?: boolean | null;
	} | null;
}

/** Decoded `RuntimeEnvironmentFactsV1` captured immediately before the spawn. */
export interface BenchmarkEnvironmentFactsWire {
	schemaVersion?: number | null;
	runtimeBundle?: {
		identity?: string | null;
		fileCount?: number | null;
		files?: Array<{ name?: string | null; sizeBytes?: number | null; lastWriteUtcTicks?: number | null }> | null;
	} | null;
	hardware?: {
		os?: string | null;
		arch?: string | null;
		cpuModel?: string | null;
		logicalCores?: number | null;
		ramBytes?: number | null;
		gpus?: Array<{ name?: string | null; totalBytes?: number | null; driverVersion?: string | null }> | null;
		deviceAuditBackend?: string | null;
	} | null;
	llamaRuntime?: {
		version?: string | null;
		variant?: string | null;
		provenance?: string | null;
		sourceCommit?: string | null;
	} | null;
	capturedAtUtc?: number | null;
	missing?: string[] | null;
}

/** Decoded evidence objects the run *detail* response adds on top of the flat columns. */
interface LaunchEvidenceDetailColumns {
	primaryLaunchReceipt?: BenchmarkLaunchReceiptWire | null;
	judgeLaunchReceipt?: BenchmarkLaunchReceiptWire | null;
	primaryEnvironmentFacts?: BenchmarkEnvironmentFactsWire | null;
	judgeEnvironmentFacts?: BenchmarkEnvironmentFactsWire | null;
}

export type BenchmarkRunSummaryWire = RunSummaryResponse & LaunchColumns<"primary"> & LaunchColumns<"judge">;
export type BenchmarkRunDetailWire = RunDetailResponse &
	LaunchColumns<"primary"> &
	LaunchColumns<"judge"> &
	LaunchEvidenceDetailColumns;

/** `POST …/runs` body. `kvCacheType` absent (or null) means Auto. */
export type StartBenchmarkRunBody = StartRunRequest & { kvCacheType?: BenchmarkKvCacheType | null };

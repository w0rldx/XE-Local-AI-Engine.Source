import type { AxiosRequestConfig } from "axios";

import { axiosInstance } from "@/core/api/axios/AxiosInstance";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import {
	type ApprovedImage,
	type ModelFitImagePurpose,
	type ModelFitLatestRecommendations,
	type ModelFitRecommendation,
	type ModelFitRecommendationFilters,
} from "@/features/model-fit/models/ModelFitModels";

// Wire DTOs (camelCase, matching the Marker 4 Local API surface). Kept as a thin contract layer so the pages
// work against the documented endpoints; if the backend casing/route base differs, only this file changes. All
// three endpoints require the Operator bearer token, which the shared axios auth-request interceptor attaches —
// the same path the scheduler feature uses. Reads are cache-only: no endpoint here runs llmfit. The DTOs carry
// only sanitized fields (no raw_json / stderr / diagnostics blobs).

export interface ApprovedImageDto {
	approvedImageId: string;
	displayName: string;
	description?: string | null;
	purpose: ModelFitImagePurpose[];
	imageReference: string;
	sourceUrl?: string | null;
	upstreamVersion?: string | null;
	enabled: boolean;
	deprecatedAtUtc?: number | null;
	replacementApprovedImageId?: string | null;
	lastUsedAtUtc?: number | null;
	lastSuccessfulRunAtUtc?: number | null;
	diagnostics?: string | null;
}

export interface ListApprovedImagesResponseDto {
	items: ApprovedImageDto[];
}

export interface ModelFitRecommendationDto {
	rank: number;
	modelName: string;
	providerModelName?: string | null;
	score: number;
	fitLevel?: string | null;
	runMode?: string | null;
	quantization?: string | null;
	estimatedTokensPerSecond?: number | null;
	requiredRamMb?: number | null;
	requiredVramMb?: number | null;
	contextTokens?: number | null;
	isInstalled: boolean;
	pullModelName?: string | null;
}

// Latest cached recommendation snapshot. hasCache:false is the empty/diagnostics state — every snapshot field is
// null and recommendations is empty. The backend returns 200 (never 404) on a cache miss.
export interface LatestRecommendationsResponseDto {
	hasCache: boolean;
	snapshotId?: string | null;
	status?: string | null;
	sourceImageId?: string | null;
	useCase?: string | null;
	providerName?: string | null;
	lastRefreshedAtUtc?: number | null;
	recommendations: ModelFitRecommendationDto[];
}

// Refresh fires an EXISTING model-recommendation-check scheduled job (it never creates one). The body carries
// only the scheduler job id; the backend rejects a missing or non-model-fit template id with a 400.
export interface RefreshRecommendationsRequestDto {
	scheduledJobId: string;
}

export interface RefreshRecommendationsResponseDto {
	scheduledJobId: string;
}

// Model-fit route base. Single source so a route mismatch from Marker 4 is a one-line change.
const MODEL_FIT_ROUTE = "model-fit";

export function toApprovedImage(dto: ApprovedImageDto): ApprovedImage {
	return {
		approvedImageId: dto.approvedImageId,
		displayName: dto.displayName,
		description: dto.description ?? null,
		purpose: dto.purpose ?? [],
		imageReference: dto.imageReference,
		sourceUrl: dto.sourceUrl ?? null,
		upstreamVersion: dto.upstreamVersion ?? null,
		enabled: dto.enabled,
		deprecatedAtUtc: dto.deprecatedAtUtc ?? null,
		replacementApprovedImageId: dto.replacementApprovedImageId ?? null,
		lastUsedAtUtc: dto.lastUsedAtUtc ?? null,
		lastSuccessfulRunAtUtc: dto.lastSuccessfulRunAtUtc ?? null,
		diagnostics: dto.diagnostics ?? null,
	};
}

export function toModelFitRecommendation(dto: ModelFitRecommendationDto): ModelFitRecommendation {
	return {
		rank: dto.rank,
		modelName: dto.modelName,
		providerModelName: dto.providerModelName ?? null,
		score: dto.score,
		fitLevel: dto.fitLevel ?? null,
		runMode: dto.runMode ?? null,
		quantization: dto.quantization ?? null,
		estimatedTokensPerSecond: dto.estimatedTokensPerSecond ?? null,
		requiredRamMb: dto.requiredRamMb ?? null,
		requiredVramMb: dto.requiredVramMb ?? null,
		contextTokens: dto.contextTokens ?? null,
		isInstalled: dto.isInstalled,
		pullModelName: dto.pullModelName ?? null,
	};
}

export function toLatestRecommendations(dto: LatestRecommendationsResponseDto): ModelFitLatestRecommendations {
	return {
		hasCache: dto.hasCache,
		snapshotId: dto.snapshotId ?? null,
		status: dto.status ?? null,
		sourceImageId: dto.sourceImageId ?? null,
		useCase: dto.useCase ?? null,
		providerName: dto.providerName ?? null,
		lastRefreshedAtUtc: dto.lastRefreshedAtUtc ?? null,
		// hasCache:false carries an empty recommendations array; coalesce defensively in case it is omitted.
		recommendations: (dto.recommendations ?? []).map(toModelFitRecommendation),
	};
}

export async function listApprovedImages(config?: AxiosRequestConfig): Promise<ApprovedImage[]> {
	const { data } = await axiosInstance.get<ListApprovedImagesResponseDto>(
		buildLocalApiUrl(`${MODEL_FIT_ROUTE}/approved-images`),
		config,
	);
	return (data.items ?? []).map(toApprovedImage);
}

// Cache-only read: returns the latest stored snapshot for the use case / provider, never running llmfit. The
// provider defaults to the backend's "ollama" when omitted.
export async function getLatestRecommendations(
	filters: ModelFitRecommendationFilters,
	config?: AxiosRequestConfig,
): Promise<ModelFitLatestRecommendations> {
	const { data } = await axiosInstance.get<LatestRecommendationsResponseDto>(
		buildLocalApiUrl(`${MODEL_FIT_ROUTE}/recommendations/latest`),
		{
			...config,
			params: {
				...config?.params,
				useCase: filters.useCase,
				...(filters.providerName !== undefined ? { providerName: filters.providerName } : {}),
			},
		},
	);
	return toLatestRecommendations(data);
}

// Fires an existing model-recommendation-check scheduled job by id. The run is async — the realtime hook
// invalidates the latest query when the run completes. A bad/non-model-fit job id rejects with a 400.
export async function refreshRecommendations(
	scheduledJobId: string,
	config?: AxiosRequestConfig,
): Promise<RefreshRecommendationsResponseDto> {
	const request: RefreshRecommendationsRequestDto = { scheduledJobId };
	const { data } = await axiosInstance.post<RefreshRecommendationsResponseDto>(
		buildLocalApiUrl(`${MODEL_FIT_ROUTE}/recommendations/refresh`),
		request,
		config,
	);
	return data;
}

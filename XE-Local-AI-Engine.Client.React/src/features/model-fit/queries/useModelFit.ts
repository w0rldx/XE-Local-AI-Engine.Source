import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	getHardwareProfileOptions,
	getLatestRecommendationsOptions,
	getModelCatalogInfoOptions,
	refreshModelCatalogMutation,
	refreshRecommendationsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toHardwareProfile, toLatestRecommendations, toModelFitCatalogInfo } from "@/features/model-fit/models/ModelFitMappers";
import type { ModelFitRecommendationFilters, ModelFitUseCase } from "@/features/model-fit/models/ModelFitModels";

// Server state for the model-fit advisor surface. Reads use the generated hey-api `*Options()` (which wire the
// shared axios instance + TanStack Query AbortSignal automatically) and a TanStack `select` that maps the
// optional-field generated response into the stricter domain view-model. Every generated options object is wrapped
// in withResponseValidation so a zod response-shape failure surfaces as an ApiError (never a raw ZodError). All
// reads are cache-only.
//
// NOTE: the GGUF browse/download, llama.cpp runtime, HF token, and running-models hooks were relocated to the
// Model Management / Node Settings / Loaded Models features (this page is now the advisor only). A download started
// from a recommendation row is OWNED by the Model Management feature — the advisor calls that feature's
// useStartGgufDownload and marks the model in the shared GGUF store, so the download surfaces (with progress +
// cancel) on the Model Management page. No download hook lives here anymore.

// The generated query keys are single-element arrays `[{ _id: "<operationId>", ... }]`. Invalidating with just the
// `_id` partial object matches every cached variant of that endpoint (TanStack partial-object matching). The
// operationIds equal the generated SDK fn names. Centralized here (and reused by useModelFitSchedulerEvents) so the
// literal `_id` key — which trips biome's naming-convention rule — is constructed in exactly one place.
export const modelFitQueryIds = {
	latest: "getLatestRecommendations",
	catalog: "getModelCatalogInfo",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one model-fit endpoint. */
export function modelFitInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

export function useLatestRecommendations(filters: ModelFitRecommendationFilters) {
	return useQuery({
		...withResponseValidation(
			getLatestRecommendationsOptions({
				query: { useCase: filters.useCase },
			}),
		),
		select: toLatestRecommendations,
	});
}

// The node hardware profile (RAM / VRAM / GPU vendor / CPU mode). `refresh:true` re-probes the box rather than
// serving the briefly-cached profile; the page passes it from the explicit "refresh hardware" action.
export function useHardwareProfile(refresh = false) {
	return useQuery({
		...withResponseValidation(getHardwareProfileOptions({ query: { refresh } })),
		select: toHardwareProfile,
	});
}

// Variables for a refresh: the existing model-recommendation-check job id, plus optional per-run overrides. When
// `useCase` is supplied the scheduler fires that job with a per-fire use-case override (validated server-side against
// the fixed six-value allowlist) so the run targets the use case the operator is viewing. `limit` widens the breadth.
export interface RefreshRecommendationsVariables {
	scheduledJobId: string;
	useCase?: ModelFitUseCase;
	limit?: number;
	quantOverride?: string;
	ctxTarget?: number;
}

// Refresh enqueues an async scheduler run, so it invalidates the latest-recommendations cache (the run may not have
// produced new data yet — useModelFitSchedulerEvents refetches again on completion). The page-facing variables stay
// domain-shaped; the hook dispatches them to the generated mutationFn's `{ body }` shape so callers never touch the wire.
export function useRefreshRecommendations() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (variables: RefreshRecommendationsVariables): Promise<void> => {
			const options = withResponseValidation(refreshRecommendationsMutation());
			await options.mutationFn?.({ body: { ...variables } }, undefined as never);
		},
		onSuccess: () => queryClient.invalidateQueries({ queryKey: modelFitInvalidationKey(modelFitQueryIds.latest) }),
	});
}

// The curated model catalog's metadata (version, source, freshness). Read-only; cache-only like the other model-fit
// reads above.
export function useModelFitCatalog() {
	return useQuery({
		...withResponseValidation(getModelCatalogInfoOptions()),
		select: toModelFitCatalogInfo,
	});
}

// Triggers a live re-fetch of the curated model catalog (no request body). On success it invalidates the catalog
// query so the page re-reads the refreshed version/source/freshness.
export function useRefreshModelFitCatalog() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async () => {
			const options = withResponseValidation(refreshModelCatalogMutation());
			return await options.mutationFn?.({}, undefined as never);
		},
		onSuccess: () => queryClient.invalidateQueries({ queryKey: modelFitInvalidationKey(modelFitQueryIds.catalog) }),
	});
}

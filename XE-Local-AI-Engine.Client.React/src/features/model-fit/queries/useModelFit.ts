import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	getLatestRecommendationsOptions,
	listApprovedImagesOptions,
	refreshRecommendationsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toApprovedImage, toLatestRecommendations } from "@/features/model-fit/models/ModelFitMappers";
import { defaultModelFitProviderName, type ModelFitRecommendationFilters } from "@/features/model-fit/models/ModelFitModels";

// Server state for the model-fit surface. Reads use the generated hey-api `*Options()` (which wire the shared
// axios instance + TanStack Query AbortSignal automatically) and a TanStack `select` that maps the optional-field
// generated response into the stricter domain view-model. Every generated options object is wrapped in
// withResponseValidation so a zod response-shape failure surfaces as an ApiError (never a raw ZodError). All reads
// are cache-only — none of them runs llmfit. The refresh mutation fires an existing scheduler job and invalidates
// the latest-recommendations cache; the run is async, so useModelFitSchedulerEvents layers refetch-on-completion.

// The generated query keys are single-element arrays `[{ _id: "<operationId>", ... }]`. Invalidating with just the
// `_id` partial object matches every cached variant of that endpoint (TanStack partial-object matching). The
// operationIds equal the generated SDK fn names. Centralized here (and reused by useModelFitSchedulerEvents) so the
// literal `_id` key — which trips biome's naming-convention rule — is constructed in exactly one place.
export const modelFitQueryIds = {
	latest: "getLatestRecommendations",
	approvedImages: "listApprovedImages",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one model-fit endpoint. */
export function modelFitInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

export function useApprovedImages() {
	return useQuery({
		...withResponseValidation(listApprovedImagesOptions()),
		select: (data) => (data.items ?? []).map(toApprovedImage),
	});
}

export function useLatestRecommendations(filters: ModelFitRecommendationFilters) {
	return useQuery({
		...withResponseValidation(
			getLatestRecommendationsOptions({
				// providerName is required on the generated query; the page only targets the single supported
				// provider, so coalesce to the backend default ("ollama") when the filter omits it.
				query: {
					useCase: filters.useCase,
					providerName: filters.providerName ?? defaultModelFitProviderName,
				},
			}),
		),
		select: toLatestRecommendations,
	});
}

function invalidateLatest(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: modelFitInvalidationKey(modelFitQueryIds.latest) });
}

// Refresh enqueues an async scheduler run, so it invalidates the latest-recommendations cache (the run may not have
// produced new data yet — useModelFitSchedulerEvents refetches again on completion). The page-facing variable stays
// the domain `scheduledJobId` string (the existing model-recommendation-check job id); the hook dispatches it to
// the generated mutationFn's `{ body: { scheduledJobId } }` shape so callers never touch the wire envelope.
export function useRefreshRecommendations() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (scheduledJobId: string): Promise<void> => {
			// Adapt the domain id to the generated `{ body }` envelope and dispatch to the generated mutationFn. The
			// response body (the echoed job id) is unused by the page, so this resolves to void.
			const options = withResponseValidation(refreshRecommendationsMutation());
			await options.mutationFn?.({ body: { scheduledJobId } }, undefined as never);
		},
		onSuccess: () => invalidateLatest(queryClient),
	});
}

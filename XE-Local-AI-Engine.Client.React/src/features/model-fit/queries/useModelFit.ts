import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	getLatestRecommendationsOptions,
	listApprovedImagesOptions,
	refreshRecommendationsMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toApprovedImage, toLatestRecommendations } from "@/features/model-fit/models/ModelFitMappers";
import {
	defaultModelFitProviderName,
	type ModelFitRecommendationFilters,
	type ModelFitUseCase,
} from "@/features/model-fit/models/ModelFitModels";

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

// Variables for a refresh: the existing model-recommendation-check job id, plus an optional use-case override. When
// `useCase` is supplied the scheduler fires that job with a per-fire use-case override (validated server-side against
// the fixed six-value allowlist) so the run targets the use case the operator is viewing — instead of the use case
// baked into the job definition. Omitting it preserves the prior baked-use-case behavior.
export interface RefreshRecommendationsVariables {
	scheduledJobId: string;
	useCase?: ModelFitUseCase;
	// Optional per-run breadth (--limit) override; validated server-side to 1..50. Widens how many candidates the run
	// returns so more pullable/installed models surface than the definition's baked default (Lane H1).
	limit?: number;
}

// Refresh enqueues an async scheduler run, so it invalidates the latest-recommendations cache (the run may not have
// produced new data yet — useModelFitSchedulerEvents refetches again on completion). The page-facing variables stay
// domain-shaped (`scheduledJobId` + optional `useCase`); the hook dispatches them to the generated mutationFn's
// `{ body: { scheduledJobId, useCase } }` shape so callers never touch the wire envelope.
export function useRefreshRecommendations() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async ({ scheduledJobId, useCase, limit }: RefreshRecommendationsVariables): Promise<void> => {
			// Adapt the domain variables to the generated `{ body }` envelope and dispatch to the generated mutationFn.
			// The response body (the echoed job id) is unused by the page, so this resolves to void.
			const options = withResponseValidation(refreshRecommendationsMutation());
			await options.mutationFn?.({ body: { scheduledJobId, useCase, limit } }, undefined as never);
		},
		onSuccess: () => invalidateLatest(queryClient),
	});
}

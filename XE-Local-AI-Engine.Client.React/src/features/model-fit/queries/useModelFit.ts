import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	getLatestRecommendations,
	listApprovedImages,
	refreshRecommendations,
} from "@/features/model-fit/api/ModelFitApi";
import type { ModelFitRecommendationFilters } from "@/features/model-fit/models/ModelFitModels";
import { modelFitQueryKeys } from "@/features/model-fit/queries/ModelFitQueryKeys";

// Server state for the model-fit surface. All reads wire the TanStack Query AbortSignal into the axios request
// (per repo React standards) and are cache-only — none of them runs llmfit. The refresh mutation fires an
// existing scheduler job and invalidates the latest-recommendations cache; the actual run is async, so the
// scheduler-run realtime hook (useModelFitSchedulerEvents) layers authoritative refetch-on-completion on top.

export function useApprovedImages() {
	return useQuery({
		queryKey: modelFitQueryKeys.approvedImages(),
		queryFn: ({ signal }) => listApprovedImages({ signal }),
	});
}

export function useLatestRecommendations(filters: ModelFitRecommendationFilters) {
	return useQuery({
		queryKey: modelFitQueryKeys.latest(filters),
		queryFn: ({ signal }) => getLatestRecommendations(filters, { signal }),
	});
}

function invalidateLatest(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: modelFitQueryKeys.latestRoot() });
}

// Refresh enqueues an async scheduler run, so it invalidates the latest-recommendations cache (the run may not
// have produced new data yet — the realtime hook refetches again on completion). The variable is the existing
// model-recommendation-check scheduled job id resolved by the page from the scheduler job list.
export function useRefreshRecommendations() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (scheduledJobId: string) => refreshRecommendations(scheduledJobId),
		onSuccess: () => invalidateLatest(queryClient),
	});
}

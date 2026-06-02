import type { ModelFitRecommendationFilters } from "@/features/model-fit/models/ModelFitModels";

// Stable, order-independent string key for the latest-recommendations filters so two equivalent filter objects
// map to the same query cache entry. The scheduler-run realtime hook invalidates by the latestRoot() prefix, so
// the exact filter shape only needs to be a deterministic suffix.
function latestFilterKey(filters: ModelFitRecommendationFilters): string {
	return JSON.stringify({
		useCase: filters.useCase,
		providerName: filters.providerName ?? null,
	});
}

export const modelFitQueryKeys = {
	all: () => ["model-fit"] as const,
	approvedImages: () => [...modelFitQueryKeys.all(), "approved-images"] as const,
	// latestRoot is the prefix shared by every (useCase, providerName) variant of the latest-recommendations
	// query, so invalidating it refetches whichever variant is currently mounted in one call.
	latestRoot: () => [...modelFitQueryKeys.all(), "recommendations", "latest"] as const,
	latest: (filters: ModelFitRecommendationFilters) =>
		[...modelFitQueryKeys.latestRoot(), latestFilterKey(filters)] as const,
};

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	benchmarkInferenceProfileMutation,
	exploreInferenceProfileMutation,
	freezeInferenceProfileMutation,
	invalidateInferenceProfileMutation,
	listInferenceProfilesOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toBenchmarkResult, toInferenceProfileViews } from "@/features/model-fit/models/InferenceProfileMappers";
import type { InferenceBenchmarkResult } from "@/features/model-fit/models/InferenceProfileModels";
import { modelFitInvalidationKey } from "@/features/model-fit/queries/useModelFit";

// Server state for the Inference Optimizer operator surface. The list read uses the generated hey-api
// `*Options()` (which wires the shared axios instance + TanStack Query AbortSignal automatically) wrapped in
// withResponseValidation, with a `select` that maps the optional-field generated response to the domain
// view-models. The four mutations dispatch the domain variables to the generated mutationFn's `{ body }` envelope
// (mirroring useRefreshRecommendations) and each invalidates the profiles list on success via the shared
// `modelFitInvalidationKey` partial-`_id` match. Errors are surfaced by the panel (toast), not here — matching the
// recommendations page convention where the component owns the toast in the mutate onError callback.

// The operationId of the list read, equal to the generated SDK fn name; the generated query key is
// `[{ _id: "listInferenceProfiles", ... }]`, so invalidating with just `{ _id }` matches every cached variant.
export const inferenceProfileQueryIds = {
	list: "listInferenceProfiles",
} as const;

export function useInferenceProfiles() {
	return useQuery({
		...withResponseValidation(listInferenceProfilesOptions()),
		select: toInferenceProfileViews,
	});
}

// Variables for an explore run. role is nullable on the wire (the backend defaults it); the panel always supplies one.
export interface ExploreInferenceProfileVariables {
	modelName: string;
	role: string | null;
}

export function useExploreInferenceProfile() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (variables: ExploreInferenceProfileVariables): Promise<void> => {
			const options = withResponseValidation(exploreInferenceProfileMutation());
			await options.mutationFn?.({ body: { modelName: variables.modelName, role: variables.role } }, undefined as never);
		},
		onSuccess: () => queryClient.invalidateQueries({ queryKey: modelFitInvalidationKey(inferenceProfileQueryIds.list) }),
	});
}

// Variables shared by the per-profile action mutations (benchmark / freeze / invalidate) — all key off the profile id.
export interface InferenceProfileActionVariables {
	profileId: string;
}

export interface BenchmarkInferenceProfileVariables extends InferenceProfileActionVariables {
	allowPreSpawnVramPressure: boolean;
}

// Benchmark returns the run's metrics; map them so the panel can render the metrics card + enrich the outcome line.
export function useBenchmarkInferenceProfile() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (variables: BenchmarkInferenceProfileVariables): Promise<InferenceBenchmarkResult> => {
			const options = withResponseValidation(benchmarkInferenceProfileMutation());
			const response = await options.mutationFn?.(
				{
					body: {
						profileId: variables.profileId,
						allowPreSpawnVramPressure: variables.allowPreSpawnVramPressure,
					},
				},
				undefined as never,
			);
			return toBenchmarkResult(response);
		},
		onSuccess: () => queryClient.invalidateQueries({ queryKey: modelFitInvalidationKey(inferenceProfileQueryIds.list) }),
	});
}

export function useFreezeInferenceProfile() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (variables: InferenceProfileActionVariables): Promise<void> => {
			const options = withResponseValidation(freezeInferenceProfileMutation());
			await options.mutationFn?.({ body: { profileId: variables.profileId } }, undefined as never);
		},
		onSuccess: () => queryClient.invalidateQueries({ queryKey: modelFitInvalidationKey(inferenceProfileQueryIds.list) }),
	});
}

export function useInvalidateInferenceProfile() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (variables: InferenceProfileActionVariables): Promise<void> => {
			const options = withResponseValidation(invalidateInferenceProfileMutation());
			await options.mutationFn?.({ body: { profileId: variables.profileId } }, undefined as never);
		},
		onSuccess: () => queryClient.invalidateQueries({ queryKey: modelFitInvalidationKey(inferenceProfileQueryIds.list) }),
	});
}

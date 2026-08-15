import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";

import {
	createComparisonMutation,
	createEvaluationMutation,
	deleteComparisonMutation,
	listComparisonsOptions,
	listEvaluationsOptions,
	resumeEvaluationMutation,
	suggestComparisonOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toComparisonReport, toComparisonSuggestion, toEvaluationRun } from "@/features/training/models/ComparisonModels";

// Server-state for evaluations and comparison reports. Evaluation progress also arrives over the training run hub
// (the evaluation rides its run's group), but a hold-out set can take minutes, so the poll below is the floor under
// it rather than the primary channel.

const comparisonQueryIds = {
	comparisons: "listComparisons",
	evaluations: "listEvaluations",
} as const;

function invalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

function invalidate(queryClient: ReturnType<typeof useQueryClient>, operationId: string): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: invalidationKey(operationId) });
}

export function useComparisonReports() {
	return useQuery({
		...withResponseValidation(listComparisonsOptions()),
		select: (data) => (data.items ?? []).map(toComparisonReport),
	});
}

/** The evaluations of one training run. Polls only while one is in flight, as a floor under the hub. */
export function useTrainingEvaluations(trainingRunId: string | null, pollWhileActive = false) {
	return useQuery({
		...withResponseValidation(listEvaluationsOptions({ query: { trainingRunId: trainingRunId ?? undefined } })),
		select: (data) => (data.items ?? []).map(toEvaluationRun),
		enabled: trainingRunId != null,
		staleTime: 0,
		refetchInterval: pollWhileActive ? 3_000 : false,
	});
}

/**
 * The lineage auto-suggest for one training run: the base and tuned model names the run implies, and the evaluations
 * that already exist for them. Read-only, so it is safe to fetch as soon as a run is picked.
 */
export function useComparisonSuggestion(trainingRunId: string | null) {
	return useQuery({
		...withResponseValidation(suggestComparisonOptions({ query: { trainingRunId: trainingRunId ?? "" } })),
		select: toComparisonSuggestion,
		enabled: trainingRunId != null,
		retry: false,
	});
}

export function useCreateEvaluation() {
	const queryClient = useQueryClient();
	return useMutation({
		...createEvaluationMutation(),
		onSuccess: async () => {
			await invalidate(queryClient, comparisonQueryIds.evaluations);
		},
	});
}

export function useResumeEvaluation() {
	const queryClient = useQueryClient();
	return useMutation({
		...resumeEvaluationMutation(),
		onSuccess: async () => {
			await invalidate(queryClient, comparisonQueryIds.evaluations);
		},
	});
}

export function useCreateComparison() {
	const queryClient = useQueryClient();
	return useMutation({
		...createComparisonMutation(),
		onSuccess: async () => {
			await Promise.all([
				invalidate(queryClient, comparisonQueryIds.comparisons),
				// Creating a report binds both evaluations, which changes their delete-ability.
				invalidate(queryClient, comparisonQueryIds.evaluations),
			]);
		},
	});
}

export function useDeleteComparison() {
	const queryClient = useQueryClient();
	return useMutation({
		...deleteComparisonMutation(),
		onSuccess: async () => {
			await Promise.all([
				invalidate(queryClient, comparisonQueryIds.comparisons),
				invalidate(queryClient, comparisonQueryIds.evaluations),
			]);
		},
	});
}

/** Refreshes the evaluation list the moment a hub event says one moved, so a finished row stops showing stale progress. */
export function useRefreshEvaluations(): () => void {
	const queryClient = useQueryClient();
	return useCallback(() => {
		// Fire-and-forget from a render effect: a failed invalidation only means the next poll refreshes the list.
		invalidate(queryClient, comparisonQueryIds.evaluations).catch(() => undefined);
	}, [queryClient]);
}

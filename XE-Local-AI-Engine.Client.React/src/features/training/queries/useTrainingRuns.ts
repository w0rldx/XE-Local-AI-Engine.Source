import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";

import {
	cancelTrainingRunMutation,
	createTrainingRunMutation,
	getTrainingRunDefaultsOptions,
	listTrainingRunsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toTrainingRunDefaultsView, toTrainingRunView } from "@/features/training/models/TrainingModels";

// Server-state for training runs. Live status, phase and step counters arrive over the run SignalR hub
// (useTrainingRunHub); the poll below is a floor under it rather than the primary channel — a run can last hours,
// so a missed push must not leave the list permanently stale.

const runQueryIds = {
	runs: "listTrainingRuns",
	defaults: "getTrainingRunDefaults",
} as const;

function runInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

function invalidate(queryClient: ReturnType<typeof useQueryClient>, operationId: string): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: runInvalidationKey(operationId) });
}

/** The run list. Polls only while a run is in flight, as a floor under the hub. */
export function useTrainingRuns(pollWhileActive = false) {
	return useQuery({
		...withResponseValidation(listTrainingRunsOptions({ query: { page: 1, pageSize: 25 } })),
		select: (data) => (data.items ?? []).map(toTrainingRunView),
		staleTime: 0,
		refetchInterval: pollWhileActive ? 5_000 : false,
	});
}

/**
 * The wizard's computed defaults for one base checkpoint: options sized to this box, the VRAM estimate behind them,
 * and the licensing text to confirm. Read-only, so it is safe to fetch as soon as a checkpoint is picked.
 */
export function useTrainingRunDefaults(baseArtifactId: string | null) {
	return useQuery({
		...withResponseValidation(getTrainingRunDefaultsOptions({ query: { baseArtifactId: baseArtifactId ?? "" } })),
		select: toTrainingRunDefaultsView,
		enabled: baseArtifactId != null,
		staleTime: 10_000,
		retry: false,
	});
}

export function useCreateTrainingRun() {
	const queryClient = useQueryClient();
	return useMutation({
		...createTrainingRunMutation(),
		onSuccess: async () => {
			await invalidate(queryClient, runQueryIds.runs);
		},
	});
}

export function useCancelTrainingRun() {
	const queryClient = useQueryClient();
	return useMutation({
		...cancelTrainingRunMutation(),
		onSuccess: async () => {
			await invalidate(queryClient, runQueryIds.runs);
		},
	});
}

/** Refreshes the run list the moment a run settles, so a finished row stops showing stale progress. */
export function useRefreshTrainingRuns(): () => void {
	const queryClient = useQueryClient();
	return useCallback(() => {
		// Fire-and-forget from a render effect: a failed invalidation only means the next poll refreshes the list.
		invalidate(queryClient, runQueryIds.runs).catch(() => undefined);
	}, [queryClient]);
}

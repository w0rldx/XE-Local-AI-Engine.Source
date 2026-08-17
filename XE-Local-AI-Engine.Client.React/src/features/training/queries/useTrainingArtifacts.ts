import { type UseMutationOptions, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";

import {
	beginTrainingArtifactQualityRevalidationMutation,
	decideTrainingArtifactQualityMutation,
	deleteTrainingArtifactMutation,
	discardTrainingArtifactQualityMutation,
	listTrainingArtifactsOptions,
	overrideTrainingArtifactQualityMutation,
	promoteTrainingArtifactMutation,
	runTrainingArtifactSmokeMutation,
	startTrainingExportMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toTrainingArtifactView } from "@/features/training/models/TrainingModels";

// Server-state for one run's staged export artifacts. An export runs for minutes and reports its phases over the run
// hub, but the artifact ROW is what carries the durable outcome — so the panel refetches this list on every export
// event rather than trying to reconstruct the row from the stream.

const artifactQueryId = "listTrainingArtifacts";

function invalidate(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return queryClient.invalidateQueries({ queryKey: [{ _id: artifactQueryId }] });
}

function useArtifactMutation<TData, TError, TVariables, TContext>(
	options: UseMutationOptions<TData, TError, TVariables, TContext>,
) {
	const queryClient = useQueryClient();
	return useMutation({
		...options,
		onSuccess: async () => {
			await invalidate(queryClient);
		},
	});
}

export function useTrainingArtifacts(runId: string | null, pollWhileExporting = false) {
	return useQuery({
		...withResponseValidation(listTrainingArtifactsOptions({ path: { runId: runId ?? "" } })),
		select: (data) => (data.items ?? []).map(toTrainingArtifactView),
		enabled: runId != null,
		staleTime: 0,
		// A floor under the hub while an export is in flight: the pipeline's phases arrive as events, but a dropped
		// one must not leave the panel showing a staged file that finished minutes ago.
		refetchInterval: pollWhileExporting ? 5_000 : false,
	});
}

/**
 * Refreshes the artifact list. Handed to the run hub alongside the run refresh: an export publishes its phases as
 * events, and the artifact row is what carries the outcome those phases describe.
 */
export function useRefreshTrainingArtifacts(): () => void {
	const queryClient = useQueryClient();
	return useCallback(() => {
		// Fire-and-forget from a stream handler: a failed invalidation only means the next poll refreshes the list.
		invalidate(queryClient).catch(() => undefined);
	}, [queryClient]);
}

export function useStartTrainingExport() {
	return useArtifactMutation(startTrainingExportMutation());
}

export function useRunTrainingArtifactSmoke() {
	return useArtifactMutation(runTrainingArtifactSmokeMutation());
}

export function usePromoteTrainingArtifact() {
	const queryClient = useQueryClient();
	return useMutation({
		...promoteTrainingArtifactMutation(),
		onSuccess: async () => {
			await invalidate(queryClient);
			// A promotion adds a local model; the models page is stale the moment it lands.
			// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
			await queryClient.invalidateQueries({ queryKey: [{ _id: "listLocalModels" }] });
		},
	});
}

export function useDecideTrainingArtifactQuality() {
	return useArtifactMutation(decideTrainingArtifactQualityMutation());
}

export function useBeginTrainingArtifactQualityRevalidation() {
	return useArtifactMutation(beginTrainingArtifactQualityRevalidationMutation());
}

export function useOverrideTrainingArtifactQuality() {
	return useArtifactMutation(overrideTrainingArtifactQualityMutation());
}

export function useDiscardTrainingArtifactQuality() {
	return useArtifactMutation(discardTrainingArtifactQualityMutation());
}

export function useDeleteTrainingArtifact() {
	return useArtifactMutation(deleteTrainingArtifactMutation());
}

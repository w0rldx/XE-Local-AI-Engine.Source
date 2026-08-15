import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useCallback } from "react";

import {
	cancelBaseArtifactMutation,
	createBaseArtifactMutation,
	deleteBaseArtifactMutation,
	getTrainingRuntimePrerequisitesOptions,
	getTrainingRuntimeStatusOptions,
	listBaseArtifactsOptions,
	removeTrainingRuntimeMutation,
	startTrainingRuntimeInstallMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toBaseArtifactView, toPrerequisitesView, toRuntimeStatusView } from "@/features/training/models/TrainingModels";

// Server-state for the training feature. Runtime install phase + log lines arrive live over the training SignalR hub
// (useTrainingRuntimeHub), which invalidates the status key on each terminal transition. Base-checkpoint download
// progress has no hub: it polls the list route while a transfer is in flight, matching the model and image download
// lanes rather than adding a third hub for a transfer that already has a status route.

const trainingQueryIds = {
	runtimeStatus: "getTrainingRuntimeStatus",
	runtimePrerequisites: "getTrainingRuntimePrerequisites",
	baseArtifacts: "listBaseArtifacts",
} as const;

/** Builds the partial generated-query-key filter matching every cached variant of one training endpoint. */
function trainingInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

export const trainingQueryKeys = {
	ids: trainingQueryIds,
	invalidationKey: trainingInvalidationKey,
};

function invalidate(queryClient: ReturnType<typeof useQueryClient>, operationId: string): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: trainingInvalidationKey(operationId) });
}

/**
 * The runtime install status. Polls only while an install is in flight, as a floor under the hub: the hub carries
 * every phase change, so the poll exists to keep the card honest if a push is missed, not as the primary channel.
 */
export function useTrainingRuntimeStatus(pollWhileInstalling = false) {
	return useQuery({
		...withResponseValidation(getTrainingRuntimeStatusOptions()),
		select: toRuntimeStatusView,
		staleTime: 0,
		refetchInterval: pollWhileInstalling ? 5_000 : false,
	});
}

/**
 * The per-item prerequisite report. Read-only server-side (it creates nothing), so it is safe to fetch on mount —
 * which is the point: the operator sees what is missing before committing to a multi-gigabyte install.
 */
export function useTrainingRuntimePrerequisites() {
	return useQuery({
		...withResponseValidation(getTrainingRuntimePrerequisitesOptions()),
		select: toPrerequisitesView,
		staleTime: 30_000,
	});
}

export function useStartTrainingRuntimeInstall() {
	const queryClient = useQueryClient();
	return useMutation({
		...startTrainingRuntimeInstallMutation(),
		onSuccess: async () => {
			await invalidate(queryClient, trainingQueryIds.runtimeStatus);
		},
	});
}

export function useRemoveTrainingRuntime() {
	const queryClient = useQueryClient();
	return useMutation({
		...removeTrainingRuntimeMutation(),
		onSuccess: async () => {
			await invalidate(queryClient, trainingQueryIds.runtimeStatus);
		},
	});
}

/** Downloaded base checkpoints. Polls while any transfer is in flight so the progress bar advances. */
export function useBaseArtifacts(pollWhileDownloading = false) {
	return useQuery({
		...withResponseValidation(listBaseArtifactsOptions()),
		select: (data) => (data.items ?? []).map(toBaseArtifactView),
		staleTime: 0,
		refetchInterval: pollWhileDownloading ? 2_000 : false,
	});
}

export function useCreateBaseArtifact() {
	const queryClient = useQueryClient();
	return useMutation({
		...createBaseArtifactMutation(),
		onSuccess: async () => {
			await invalidate(queryClient, trainingQueryIds.baseArtifacts);
		},
	});
}

export function useDeleteBaseArtifact() {
	const queryClient = useQueryClient();
	return useMutation({
		...deleteBaseArtifactMutation(),
		onSuccess: async () => {
			await invalidate(queryClient, trainingQueryIds.baseArtifacts);
		},
	});
}

export function useCancelBaseArtifact() {
	const queryClient = useQueryClient();
	return useMutation({
		...cancelBaseArtifactMutation(),
		onSuccess: async () => {
			await invalidate(queryClient, trainingQueryIds.baseArtifacts);
		},
	});
}

/** Refreshes the artifact list the moment a transfer settles, so the row stops showing stale progress. */
export function useRefreshBaseArtifacts(): () => void {
	const queryClient = useQueryClient();
	return useCallback(() => {
		// Fire-and-forget from a render effect: a failed invalidation only means the next poll refreshes the list.
		invalidate(queryClient, trainingQueryIds.baseArtifacts).catch(() => undefined);
	}, [queryClient]);
}

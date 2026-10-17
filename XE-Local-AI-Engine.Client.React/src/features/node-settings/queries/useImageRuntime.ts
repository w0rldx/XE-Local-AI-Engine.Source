import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	cancelStableDiffusionCppSourceBuildMutation,
	ejectImageRuntimeMutation,
	getImageRuntimeStatusOptions,
	getStableDiffusionCppSourceBuildPrerequisitesOptions,
	getStableDiffusionCppSourceBuildStatusOptions,
	removeStableDiffusionCppSourceBuildMutation,
	startStableDiffusionCppSourceBuildMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import {
	toImageRuntimeSourceBuildPrerequisites,
	toImageRuntimeSourceBuildStatus,
	toImageRuntimeStatus,
} from "@/features/node-settings/models/ImageRuntimeSourceBuildMappers";
import type {
	ImageRuntimeSourceBackend,
	ImageRuntimeSourceBuildDraft,
} from "@/features/node-settings/models/ImageRuntimeSourceBuildModels";
import { sourceBuildPrerequisiteStaleTime, sourceBuildRequest } from "@/features/node-settings/models/SourceBuildModels";
import { localRuntimeInvalidationKey } from "@/features/node-settings/queries/useLocalRuntime";

export const imageRuntimeQueryIds = {
	runtime: "getImageRuntimeStatus",
	sourceBuildPrerequisites: "getStableDiffusionCppSourceBuildPrerequisites",
	sourceBuildStatus: "getStableDiffusionCppSourceBuildStatus",
} as const;

function invalidate(queryClient: ReturnType<typeof useQueryClient>, operationId: string): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: localRuntimeInvalidationKey(operationId) });
}

export function useImageRuntimeStatus(enabled = true) {
	return useQuery({
		...getImageRuntimeStatusOptions(),
		select: toImageRuntimeStatus,
		enabled,
	});
}

export function useImageRuntimeSourceBuildPrerequisites(backend: ImageRuntimeSourceBackend, enabled = true) {
	return useQuery({
		...getStableDiffusionCppSourceBuildPrerequisitesOptions({ query: { backend } }),
		select: toImageRuntimeSourceBuildPrerequisites,
		enabled,
		staleTime: sourceBuildPrerequisiteStaleTime,
	});
}

export function useImageRuntimeSourceBuildStatus(enabled = true) {
	return useQuery({
		...getStableDiffusionCppSourceBuildStatusOptions(),
		select: toImageRuntimeSourceBuildStatus,
		enabled,
	});
}

export function useStartImageRuntimeSourceBuild() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (draft: ImageRuntimeSourceBuildDraft) => {
			const options = startStableDiffusionCppSourceBuildMutation();
			return await options.mutationFn?.({ body: sourceBuildRequest(draft) }, undefined as never);
		},
		onSuccess: () =>
			Promise.all([
				invalidate(queryClient, imageRuntimeQueryIds.sourceBuildStatus),
				invalidate(queryClient, imageRuntimeQueryIds.runtime),
			]),
	});
}

export function useCancelImageRuntimeSourceBuild() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async () => {
			const options = cancelStableDiffusionCppSourceBuildMutation();
			return await options.mutationFn?.({ body: {} }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, imageRuntimeQueryIds.sourceBuildStatus),
	});
}

export function useRemoveImageRuntimeSourceBuild() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async () => {
			const options = removeStableDiffusionCppSourceBuildMutation();
			return await options.mutationFn?.({ body: {} }, undefined as never);
		},
		onSuccess: () =>
			Promise.all([
				invalidate(queryClient, imageRuntimeQueryIds.sourceBuildStatus),
				invalidate(queryClient, imageRuntimeQueryIds.runtime),
			]),
	});
}

export function useEjectImageRuntime() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async () => {
			const options = ejectImageRuntimeMutation();
			return await options.mutationFn?.({ body: {} }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, imageRuntimeQueryIds.runtime),
	});
}

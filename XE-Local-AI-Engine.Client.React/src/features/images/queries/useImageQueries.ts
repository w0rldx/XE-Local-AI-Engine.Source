import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	cancelImageJobMutation,
	createImageJobMutation,
	listImageJobsOptions,
	listImageModelsOptions,
	startImageModelDownloadMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import type {
	XeLocalAiEngineClientEndpointsImagesV1CreateImageJobRequest as CreateImageJobRequest,
	XeLocalAiEngineClientEndpointsImagesV1StartImageModelDownloadRequest as StartImageModelDownloadRequest,
} from "@/core/api/generated";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toImageJobView, toImageModelView } from "@/features/images/models/ImageModels";

// Server-state for the image feature. Reads use the generated hey-api `*Options()` (shared axios + TanStack
// AbortSignal wired automatically) with a `select` mapping the optional-field generated response into the strict
// domain view-model. Every options object is wrapped in withResponseValidation so a zod response-shape failure
// surfaces as an ApiError. Job state lives ONLY in TanStack Query (never mirrored into a store, plan §9); the
// SignalR hub (useImageJobHub) drives cache invalidation on each coarse status push.

// The generated query keys are single-element arrays `[{ _id: "<operationId>", ... }]`. Invalidating with just the
// `_id` partial object matches every cached variant of that endpoint. Centralized here so the literal `_id` key —
// which trips biome's naming-convention rule — is constructed in exactly one place.
const imageQueryIds = {
	jobs: "listImageJobs",
	models: "listImageModels",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one image endpoint. */
function imageInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

function invalidate(queryClient: ReturnType<typeof useQueryClient>, operationId: string): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: imageInvalidationKey(operationId) });
}

// Installed image models backing the generation form's model picker + the minimal model manager. staleTime keeps a
// remount within the session from refetching; the download mutation invalidates this key so a freshly downloaded
// model appears without a manual refresh.
export function useImageModels() {
	return useQuery({
		...withResponseValidation(listImageModelsOptions()),
		select: (data) => (data.items ?? []).map(toImageModelView),
		staleTime: 30_000,
	});
}

// All image jobs, newest-first. The hub push invalidates this key on every coarse status transition, so no polling is
// needed; a refetchInterval fallback keeps the coarse "elapsed" fresh even if the hub is momentarily down.
export function useImageJobs() {
	return useQuery({
		...withResponseValidation(listImageJobsOptions()),
		select: (data) => (data.items ?? []).map(toImageJobView).sort((a, b) => b.createdAtUtc - a.createdAtUtc),
	});
}

// Creates (enqueues) an image job. On success the job list gains the queued job, so invalidate it — the hub then
// pushes subsequent coarse transitions. Returns the wire response so the caller can read the new job id.
export function useCreateImageJob() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (body: CreateImageJobRequest) => {
			const options = withResponseValidation(createImageJobMutation());
			return await options.mutationFn?.({ body }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, imageQueryIds.jobs),
	});
}

// Cancels a job (coordinator picks clean-cancel for queued vs daemon-restart for generating). Invalidates the job
// list so the cancelled row reflects immediately even before the hub push lands.
export function useCancelImageJob() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (jobId: string) => {
			const options = withResponseValidation(cancelImageJobMutation());
			return await options.mutationFn?.({ path: { jobId } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, imageQueryIds.jobs),
	});
}

// Starts a detached image-model weight download (202 accepted). The full download-progress hub is a follow-up
// (plan §8) — for now the model manager polls listImageModels for the model appearing, so invalidate that key.
export function useStartImageModelDownload() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (body: StartImageModelDownloadRequest) => {
			const options = withResponseValidation(startImageModelDownloadMutation());
			return await options.mutationFn?.({ body }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, imageQueryIds.models),
	});
}

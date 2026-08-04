import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	browseImageRepositoriesOptions,
	cancelImageJobMutation,
	cancelImageModelDownloadMutation,
	createImageJobMutation,
	deleteImageModelMutation,
	getImageModelCatalogOptions,
	inspectImageRepositoryOptions,
	listImageJobsOptions,
	listImageModelDownloadsOptions,
	listImageModelsOptions,
	startImageModelDownloadMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import type {
	XeLocalAiEngineClientEndpointsImagesV1CreateImageJobRequest as CreateImageJobRequest,
	XeLocalAiEngineClientEndpointsImagesV1StartImageModelDownloadRequest as StartImageModelDownloadRequest,
} from "@/core/api/generated";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import {
	toImageJobView,
	toImageModelCatalogEntryView,
	toImageModelDownloadView,
	toImageModelView,
	toImageRepositoryFileView,
	toImageRepositoryView,
} from "@/features/images/models/ImageModels";

// Server-state for the image feature. Reads use the generated hey-api `*Options()` (shared axios + TanStack
// AbortSignal wired automatically) with a `select` mapping the optional-field generated response into the strict
// domain view-model. Every options object is wrapped in withResponseValidation so a zod response-shape failure
// surfaces as an ApiError. Job state lives ONLY in TanStack Query (never mirrored into a store); the
// SignalR hub (useImageJobHub) drives cache invalidation on each coarse status push.

// The generated query keys are single-element arrays `[{ _id: "<operationId>", ... }]`. Invalidating with just the
// `_id` partial object matches every cached variant of that endpoint. Centralized here so the literal `_id` key —
// which trips biome's naming-convention rule — is constructed in exactly one place.
const imageQueryIds = {
	jobs: "listImageJobs",
	models: "listImageModels",
	downloads: "listImageModelDownloads",
	catalog: "getImageModelCatalog",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one image endpoint. */
function imageInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

function invalidate(queryClient: ReturnType<typeof useQueryClient>, operationId: string): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: imageInvalidationKey(operationId) });
}

// Tracked image-model weight downloads (in flight + recently finished). This is the surface that makes a FAILED
// download visible: while one is pending the caller polls, and a Failed phase carries the sanitized reason. Never
// cached between polls — a stale "Running" would recreate exactly the silent-hang bug this query exists to kill.
export function useImageModelDownloads(pollWhilePending = false) {
	return useQuery({
		...withResponseValidation(listImageModelDownloadsOptions()),
		select: (data) => (data.items ?? []).map(toImageModelDownloadView),
		enabled: pollWhilePending,
		staleTime: 0,
		refetchInterval: pollWhilePending ? 2_000 : false,
	});
}

// Installed image models backing the generation form's model picker + the minimal model manager. staleTime keeps a
// remount within the session from refetching; the download mutation invalidates this key so a freshly downloaded
// model appears without a manual refresh. While a download is in flight the caller passes `pollWhilePending` to
// enable a modest interval refetch — that is how the freshly-downloaded model surfaces on completion without a
// manual refresh.
export function useImageModels(pollWhilePending = false) {
	return useQuery({
		...withResponseValidation(listImageModelsOptions()),
		select: (data) => (data.items ?? []).map(toImageModelView),
		staleTime: 30_000,
		refetchInterval: pollWhilePending ? 5_000 : false,
	});
}

// The curated image-model catalog: the one-click install list, annotated by the backend with this box's hardware fit
// and an installed flag. Both annotations are derived server-side from the registry and the hardware probe.
//
// `pollWhilePending` exists because the start mutation's invalidation fires on the 202 — at which point the model is
// emphatically NOT installed yet. Without a poll the row an operator just clicked keeps offering Install for the whole
// transfer and until the next manual refresh, which invites a second download of weights already landing on disk.
export function useImageModelCatalog(pollWhilePending = false) {
	return useQuery({
		...withResponseValidation(getImageModelCatalogOptions()),
		select: (data) => (data.items ?? []).map(toImageModelCatalogEntryView),
		staleTime: 30_000,
		refetchInterval: pollWhilePending ? 5_000 : false,
	});
}

// Hugging Face image-repository search. Disabled until the user commits a search term: the trending list is not what
// somebody who opened this panel is looking for, and an unprompted fetch on mount costs a Hub round trip per page view.
export function useBrowseImageRepositories(query: string) {
	return useQuery({
		...withResponseValidation(browseImageRepositoriesOptions({
			query: { query, limit: 20 },
		})),
		select: (data) => (data.items ?? []).map(toImageRepositoryView),
		enabled: query.trim().length > 0,
		staleTime: 60_000,
	});
}

// One repository's selectable weight files. Enabled only once a repo is chosen, because inspection is a second Hub
// round trip per repo and the browse table lists twenty of them.
export function useInspectImageRepository(repoId: string | null) {
	return useQuery({
		...withResponseValidation(inspectImageRepositoryOptions({
			query: { repoId: repoId ?? "" },
		})),
		select: (data) => ({
			repoId: data.repoId,
			isGated: data.isGated,
			license: data.license ?? null,
			files: (data.files ?? []).map(toImageRepositoryFileView),
		}),
		enabled: repoId !== null && repoId.trim().length > 0,
		staleTime: 60_000,
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

// Starts a detached image-model weight download (202 accepted). The coordinator then owns the transfer and its terminal
// phase; the manager polls useImageModelDownloads for the outcome. Both the models list and the downloads list are
// invalidated so the newly accepted download shows up immediately.
export function useStartImageModelDownload() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (body: StartImageModelDownloadRequest) => {
			const options = withResponseValidation(startImageModelDownloadMutation());
			return await options.mutationFn?.({ body }, undefined as never);
		},
		onSuccess: async () => {
			await invalidate(queryClient, imageQueryIds.models);
			await invalidate(queryClient, imageQueryIds.downloads);
			// The catalog carries a server-derived "installed" flag, so it goes stale the moment a download is accepted.
			await invalidate(queryClient, imageQueryIds.catalog);
		},
	});
}

// Cancels an in-flight file-set pull. Idempotent by contract — a 200 with `cancelled: false` means the download had
// already finished, which is a race on a stale row rather than an error. Invalidates the downloads list so the row
// reflects the terminal phase without waiting for the next poll tick.
export function useCancelImageModelDownload() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (modelName: string) => {
			const options = withResponseValidation(cancelImageModelDownloadMutation());
			return await options.mutationFn?.({ body: { modelName } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, imageQueryIds.downloads),
	});
}

// Removes an installed model's weights and registry entry. Without it a node that has installed several multi-gigabyte
// file-sets has no in-app way to reclaim the disk. Invalidates the installed-models list so the row disappears.
export function useDeleteImageModel() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (modelName: string) => {
			const options = withResponseValidation(deleteImageModelMutation());
			return await options.mutationFn?.({ path: { modelName } }, undefined as never);
		},
		onSuccess: async () => {
			await invalidate(queryClient, imageQueryIds.models);
			// Deleting frees the catalog row to offer Install again.
			await invalidate(queryClient, imageQueryIds.catalog);
		},
	});
}

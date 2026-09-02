import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useMemo } from "react";

import {
	browseGgufRepositoriesOptions,
	cancelGgufDownloadMutation,
	getGgufDownloadsQueryKey,
	inspectGgufRepositoryOptions,
	startGgufDownloadMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toGgufRepository, toGgufRepositoryDetail } from "@/features/models/models/GgufMappers";
import type { GgufAcquisitionStatus } from "@/features/models/models/GgufAcquisitionModels";
import { useActiveGgufAcquisitions } from "@/features/models/queries/useGgufAcquisitions";
import { useGgufBrowseStore } from "@/features/models/stores/GgufBrowseStore";

// Server state for the Hugging Face GGUF browse + download flow on the Model Management page. Reads use the generated
// hey-api `*Options()` (which wire the shared axios instance + TanStack Query AbortSignal automatically) and a
// TanStack `select` that maps the optional-field generated response into the stricter domain view-model. Every
// generated options object is wrapped in withResponseValidation so a zod response-shape failure surfaces as an
// ApiError (never a raw ZodError). Mutations adapt the domain variables to the generated `{ body }` envelope and
// invalidate the caches they affect.

// Generated keys are object arrays; TanStack partial matching on `_id` invalidates every endpoint variant.
const ggufQueryIds = {
	browse: "browseGgufRepositories",
	inspect: "inspectGgufRepository",
	// A started/cancelled download changes the llama.cpp running-models list (it surfaces the in-flight download), so
	// the download mutations invalidate that list even though it now renders on the Loaded Models page — TanStack
	// query keys are global, so the cross-page cache refetch still works.
	runningModels: "listRunningModels",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one GGUF endpoint. */
function ggufInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

function invalidate(queryClient: ReturnType<typeof useQueryClient>, operationId: string): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: ggufInvalidationKey(operationId) });
}

// GGUF repository browse search. enabled gates the query so it only fires once the operator has entered a search term
// (an empty browse is not run). The query string scopes the cache key so each search caches independently.
export function useBrowseGgufRepositories(query: string, enabled: boolean) {
	const trimmed = query.trim();
	return useQuery({
		...withResponseValidation(browseGgufRepositoriesOptions({ query: { query: trimmed } })),
		select: (data) => (data.items ?? []).map(toGgufRepository),
		enabled: enabled && trimmed.length > 0,
	});
}

// Per-repo GGUF file inspection backing the quant picker. enabled gates the query so it only fires when a repo is
// selected (the download dialog is open). The repo id scopes the cache key so each repo's quant list caches
// independently. A discovery failure returns a 200 empty file list (handled by the dialog as "no files").
export function useInspectGgufRepository(repoId: string, enabled: boolean) {
	const trimmed = repoId.trim();
	return useQuery({
		...withResponseValidation(inspectGgufRepositoryOptions({ query: { repoId: trimmed } })),
		select: toGgufRepositoryDetail,
		enabled: enabled && trimmed.length > 0,
	});
}

// Variables for starting a GGUF download. fileName / quant / revision are optional — when omitted the backend picks
// the default quant (Q4_K_M) GGUF file from the repo.
export interface StartGgufDownloadVariables {
	repoId: string;
	fileName?: string;
	quant?: string;
	revision?: string;
}

// Starts a resumable GGUF download via the Hugging Face GGUF model store. On success the running-models list may
// change as the download begins, so invalidate it. Returns the wire response so the caller can read
// `alreadyInFlight` / the resolved `modelName`.
export function useStartGgufDownload() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (variables: StartGgufDownloadVariables) => {
			const options = withResponseValidation(startGgufDownloadMutation());
			return await options.mutationFn?.({ body: { ...variables } }, undefined as never);
		},
		// Invalidate the downloads list so the active-downloads poll refetches immediately and re-arms its interval — the
		// poll stops once nothing is Running, so a download started from idle would otherwise never be picked up until a
		// remount (the "Downloading… forever" bug). Also refresh the running-models list the in-flight download surfaces in.
		onSuccess: () =>
			Promise.all([
				invalidate(queryClient, ggufQueryIds.runningModels),
				queryClient.invalidateQueries({ queryKey: getGgufDownloadsQueryKey() }),
			]),
	});
}

// Cancels an in-flight GGUF download by model name. Invalidates the running-models list so the cancelled entry clears.
export function useCancelGgufDownload() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (modelName: string) => {
			const options = withResponseValidation(cancelGgufDownloadMutation());
			return await options.mutationFn?.({ body: { modelName } }, undefined as never);
		},
		onSuccess: () =>
			Promise.all([
				invalidate(queryClient, ggufQueryIds.runningModels),
				queryClient.invalidateQueries({ queryKey: getGgufDownloadsQueryKey() }),
			]),
	});
}

// Domain view-model for a single active GGUF download, derived from the backend DTO.
export type GgufDownloadStatus = GgufAcquisitionStatus & {
	readonly operationKind: "Download";
	readonly sanitizedError: string | null | undefined;
};

// Live GGUF download progress. Does ONE initial fetch of GET model-fit/gguf/downloads to hydrate on mount (no polling),
// then opens the GgufDownloadHub SignalR connection and merges each pushed status into a local map — replacing the old
// per-second poll. Reconciles GgufBrowseStore so:
//   – downloads started before a navigation refresh rehydrate into the store (via the one-shot hydrate + a re-fetch the
//     start/cancel mutations trigger by invalidating the list key);
//   – models no longer Running are removed from the store.
// Returns a map of modelName → GgufDownloadStatus for all non-idle entries known (hydrate ∪ live pushes).
//
// `enabled` (default true) gates BOTH the hydrate query and the hub. Pass `false` to suppress them pre-auth — the
// GgufDownloadPoller uses this to avoid a 401 before login (mirrors useTourState / useSchedulerHub auth-gating).
export function useActiveGgufDownloads({ enabled = true }: { enabled?: boolean } = {}): ReadonlyMap<string, GgufDownloadStatus> {
	const markInFlight = useGgufBrowseStore((state) => state.actions.markInFlight);
	const removeInFlight = useGgufBrowseStore((state) => state.actions.removeInFlight);
	const acquisitions = useActiveGgufAcquisitions({ enabled });
	const statuses = useMemo(() => {
		const downloads = new Map<string, GgufDownloadStatus>();
		for (const status of acquisitions.values()) {
			if (status.operationKind === "Download") {
				downloads.set(status.modelName, {
					...status,
					operationKind: "Download",
					sanitizedError: status.sanitizedMessage,
				});
			}
		}
		return downloads;
	}, [acquisitions]);

	// Reconcile the store with the live map: Running → markInFlight (show progress), terminal → removeInFlight (clear).
	// On a Completed download, also invalidate the installed-models list so the freshly downloaded GGUF appears without a
	// page refresh — completion has no REST mutation to hang invalidation off, so this push-driven reconcile is the only
	// hook point. Guarded by completedHandled so a re-pushed/re-hydrated Completed status refetches exactly once.
	useEffect(() => {
		for (const status of statuses.values()) {
			if (status.phase === "Queued" || status.phase === "Validating" || status.phase === "Downloading" || status.phase === "Committing") {
				markInFlight(status.modelName);
				continue;
			}
			removeInFlight(status.modelName);
		}
	}, [statuses, markInFlight, removeInFlight]);

	return statuses;
}

import { useEffect } from "react";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	browseGgufRepositoriesOptions,
	cancelGgufDownloadMutation,
	getGgufDownloadsOptions,
	getGgufDownloadsQueryKey,
	inspectGgufRepositoryOptions,
	startGgufDownloadMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toGgufRepository, toGgufRepositoryDetail } from "@/features/models/models/GgufMappers";
import { useGgufBrowseStore } from "@/features/models/stores/GgufBrowseStore";

// Server state for the Hugging Face GGUF browse + download flow on the Model Management page. Reads use the generated
// hey-api `*Options()` (which wire the shared axios instance + TanStack Query AbortSignal automatically) and a
// TanStack `select` that maps the optional-field generated response into the stricter domain view-model. Every
// generated options object is wrapped in withResponseValidation so a zod response-shape failure surfaces as an
// ApiError (never a raw ZodError). Mutations adapt the domain variables to the generated `{ body }` envelope and
// invalidate the caches they affect.

// The generated query keys are single-element arrays `[{ _id: "<operationId>", ... }]`. Invalidating with just the
// `_id` partial object matches every cached variant of that endpoint (TanStack partial-object matching). The
// operationIds equal the generated SDK fn names. Centralized here so the literal `_id` key — which trips biome's
// naming-convention rule — is constructed in exactly one place.
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
export interface GgufDownloadStatus {
	modelName: string;
	phase: "Running" | "Completed" | "Cancelled" | "Failed";
	/** 0–100 when totalBytes is known; undefined when Content-Length is absent (indeterminate). */
	pct: number | undefined;
	completedBytes: number | null | undefined;
	totalBytes: number | null | undefined;
	sanitizedError: string | null | undefined;
}

// Polls GET /api/local/v1/model-fit/gguf/downloads (1 s interval while any download is Running).
// Reconciles the backend list with GgufBrowseStore so:
//   – downloads started before a navigation refresh rehydrate into the store;
//   – models no longer Running are removed from the store.
// Returns a map of modelName → GgufDownloadStatus for all non-idle entries the backend reports.
//
// `enabled` (default true) gates the underlying query. Pass `false` to suppress the poll
// pre-auth — the GgufDownloadPoller uses this to avoid a 401 before login (mirrors useTourState).
export function useActiveGgufDownloads({ enabled = true }: { enabled?: boolean } = {}): ReadonlyMap<string, GgufDownloadStatus> {
	const markInFlight = useGgufBrowseStore((state) => state.actions.markInFlight);
	const removeInFlight = useGgufBrowseStore((state) => state.actions.removeInFlight);
	// Number of downloads started this session (per the store). Read here so this hook re-renders when a download is
	// kicked off — that re-render lets TanStack re-evaluate refetchInterval and resume polling from idle.
	const inFlightCount = useGgufBrowseStore((state) => state.inFlightDownloads.length);

	const { data } = useQuery({
		...withResponseValidation(getGgufDownloadsOptions()),
		enabled,
		// Poll every second while EITHER the backend reports a Running download OR the store still tracks an in-flight one.
		// Gating only on the query's own last data was a chicken-and-egg bug: once the poll saw no Running items it returned
		// `false` and stopped, so a download STARTED from that idle state was never picked up (the poll never re-ran to see
		// it) and the row showed "Downloading…" forever until a remount. Including the store's in-flight count keeps the
		// poll alive across the start→first-Running window; it winds down once the backend reports terminal AND the store
		// has been reconciled empty.
		refetchInterval: (query) => {
			const items = query.state.data?.items;
			const hasRunning = items?.some((item) => item.phase === "Running") ?? false;
			return hasRunning || inFlightCount > 0 ? 1000 : false;
		},
	});

	const items = data?.items ?? [];

	// Reconcile store with backend list on every render where data has updated.
	useEffect(() => {
		for (const item of items) {
			if (item.phase === "Running") {
				if (item.modelName) {
					markInFlight(item.modelName);
				}
			} else if (item.modelName) {
				removeInFlight(item.modelName);
			}
		}
	}, [items, markInFlight, removeInFlight]);

	// Build view-model map from all items (Running or terminal) so the panel can show
	// progress for Running and error state for Failed.
	const map = new Map<string, GgufDownloadStatus>();
	for (const item of items) {
		if (!item.modelName) { continue; }
		const pct =
			item.totalBytes && item.completedBytes != null
				? Math.round((item.completedBytes / item.totalBytes) * 100)
				: undefined;
		map.set(item.modelName, {
			modelName: item.modelName,
			phase: (item.phase ?? "Running") as GgufDownloadStatus["phase"],
			pct,
			completedBytes: item.completedBytes,
			totalBytes: item.totalBytes,
			sanitizedError: item.sanitizedError,
		});
	}
	return map;
}

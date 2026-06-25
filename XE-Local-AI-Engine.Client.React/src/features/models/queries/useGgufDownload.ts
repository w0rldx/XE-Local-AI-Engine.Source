import { HubConnectionBuilder, LogLevel } from "@microsoft/signalr";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";
import { useEffect, useRef, useState } from "react";

import {
	browseGgufRepositoriesOptions,
	cancelGgufDownloadMutation,
	getGgufDownloadsOptions,
	getGgufDownloadsQueryKey,
	inspectGgufRepositoryOptions,
	listLocalModelsQueryKey,
	startGgufDownloadMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { buildLocalApiUrl } from "@/core/api/utils/LocalApiUrl";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
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

// The single SignalR client-method name the GgufDownloadHub invokes. Each push carries the full sanitized status, so the
// client reconciles by model name with no follow-up REST poll. Must match GgufDownloadHubEvents.StatusChanged.
const DOWNLOAD_STATUS_CHANGED = "ggufDownload.statusChanged";

// The sanitized status push payload — mirrors the REST GgufDownloadStatusResponse field-for-field (PascalCase off the
// wire is normalized to camelCase by the SignalR JSON protocol the server configures, matching the generated REST DTO).
interface GgufDownloadStatusPush {
	modelName?: string | null;
	phase?: string | null;
	completedBytes?: number | null;
	totalBytes?: number | null;
	sanitizedError?: string | null;
}

// Maps a raw status (REST list item or hub push, identical shape) into the strict domain view-model. Returns null when
// the model name is absent (nothing to key on).
function toDownloadStatus(raw: GgufDownloadStatusPush): GgufDownloadStatus | null {
	if (!raw.modelName) {
		return null;
	}
	const pct = raw.totalBytes && raw.completedBytes != null ? Math.round((raw.completedBytes / raw.totalBytes) * 100) : undefined;
	return {
		modelName: raw.modelName,
		phase: (raw.phase ?? "Running") as GgufDownloadStatus["phase"],
		pct,
		completedBytes: raw.completedBytes,
		totalBytes: raw.totalBytes,
		sanitizedError: raw.sanitizedError,
	};
}

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
	const queryClient = useQueryClient();

	// Model names whose terminal "Completed" status we've already reacted to. A completed download adds a file to the
	// local model store, so we invalidate the installed-models list — but completion arrives ONLY as a hub push (no REST
	// mutation fires), and that same status re-arrives on hub reconnect / hydrate refetch. Tracking handled names keeps
	// the invalidation to exactly once per model instead of on every reconcile render.
	const completedHandled = useRef<Set<string>>(new Set());

	// Live merged status map: seeded from the one-shot hydrate, then updated in place by each hub push.
	const [statuses, setStatuses] = useState<ReadonlyMap<string, GgufDownloadStatus>>(() => new Map());

	// One-shot hydrate on mount. No refetchInterval — live updates arrive over the hub. staleTime keeps a remount within
	// the session from refetching; the start/cancel mutations invalidate this key to force a fresh hydrate when needed.
	const { data } = useQuery({
		...withResponseValidation(getGgufDownloadsOptions()),
		enabled,
		staleTime: 30_000,
	});

	// Merge the hydrate snapshot into the live map (without clobbering newer hub pushes for the same model: a push that
	// arrived first stays unless the hydrate carries a different — i.e. more recent on (re)fetch — phase/bytes).
	useEffect(() => {
		const items = data?.items;
		if (!items || items.length === 0) {
			return;
		}
		setStatuses((previous) => {
			const next = new Map(previous);
			for (const item of items) {
				const status = toDownloadStatus(item);
				if (status) {
					next.set(status.modelName, status);
				}
			}
			return next;
		});
	}, [data]);

	// Live push channel. Mounted for the hook's lifetime (the poller mounts it globally), gated on auth like the
	// scheduler hub. Each push replaces that model's entry in the live map. StrictMode-safe connect/disconnect mirrors
	// useSchedulerHub: stop only after start settles, tolerate a start aborted by our own cleanup.
	useEffect(() => {
		if (!enabled) {
			return;
		}

		const connection = new HubConnectionBuilder()
			.withUrl(buildLocalApiUrl("model-fit/gguf/downloads/hub"), {
				accessTokenFactory: () => useNodeAuthStore.getState().accessToken ?? "",
			})
			.withAutomaticReconnect()
			.configureLogging(LogLevel.Warning)
			.build();

		const onStatus = (push: GgufDownloadStatusPush): void => {
			const status = toDownloadStatus(push);
			if (!status) {
				return;
			}
			setStatuses((previous) => {
				const next = new Map(previous);
				next.set(status.modelName, status);
				return next;
			});
		};

		connection.on(DOWNLOAD_STATUS_CHANGED, onStatus);

		let disposed = false;
		const startPromise = connection.start().catch((error: unknown) => {
			// A start aborted by our own cleanup (StrictMode double-invoke / fast remount) is not a real failure.
			if (disposed) {
				return;
			}
			// A hub that cannot connect must not break the page — the one-shot hydrate still seeded the last known state.
			console.warn("gguf download hub failed to start", error);
		});

		return () => {
			disposed = true;
			connection.off(DOWNLOAD_STATUS_CHANGED, onStatus);
			// Stop only AFTER start settles so cleanup never aborts an in-flight negotiation (the StrictMode race).
			startPromise.finally(() => {
				connection.stop().catch((error: unknown) => {
					console.warn("gguf download hub failed to stop", error);
				});
			});
		};
	}, [enabled]);

	// Reconcile the store with the live map: Running → markInFlight (show progress), terminal → removeInFlight (clear).
	// On a Completed download, also invalidate the installed-models list so the freshly downloaded GGUF appears without a
	// page refresh — completion has no REST mutation to hang invalidation off, so this push-driven reconcile is the only
	// hook point. Guarded by completedHandled so a re-pushed/re-hydrated Completed status refetches exactly once.
	useEffect(() => {
		for (const status of statuses.values()) {
			if (status.phase === "Running") {
				markInFlight(status.modelName);
				continue;
			}
			removeInFlight(status.modelName);
			if (status.phase === "Completed" && !completedHandled.current.has(status.modelName)) {
				completedHandled.current.add(status.modelName);
				queryClient.invalidateQueries({ queryKey: listLocalModelsQueryKey() }).catch(() => undefined);
			}
		}
	}, [statuses, markInFlight, removeInFlight, queryClient]);

	return statuses;
}

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	browseGgufRepositoriesOptions,
	cancelGgufDownloadMutation,
	ejectRunningModelMutation,
	ensureLlamaCppBinaryMutation,
	getHardwareProfileOptions,
	getHfTokenStatusOptions,
	getLatestRecommendationsOptions,
	getLlamaCppVersionOptions,
	inspectGgufRepositoryOptions,
	listRunningModelsOptions,
	refreshRecommendationsMutation,
	setHfTokenMutation,
	startGgufDownloadMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import {
	toGgufRepository,
	toGgufRepositoryDetail,
	toHardwareProfile,
	toLatestRecommendations,
	toLlamaCppVersion,
	toRunningModel,
} from "@/features/model-fit/models/ModelFitMappers";
import type {
	LlamaCppVariant,
	ModelFitRecommendationFilters,
	ModelFitUseCase,
} from "@/features/model-fit/models/ModelFitModels";

// Server state for the model-fit advisor surface. Reads use the generated hey-api `*Options()` (which wire the
// shared axios instance + TanStack Query AbortSignal automatically) and a TanStack `select` that maps the
// optional-field generated response into the stricter domain view-model. Every generated options object is wrapped
// in withResponseValidation so a zod response-shape failure surfaces as an ApiError (never a raw ZodError). All
// reads are cache-only. Mutations adapt the domain variables to the generated `{ body }` envelope and invalidate the
// caches they affect; the HF token is write-only — it is never read back (only its boolean status is fetched).

// The generated query keys are single-element arrays `[{ _id: "<operationId>", ... }]`. Invalidating with just the
// `_id` partial object matches every cached variant of that endpoint (TanStack partial-object matching). The
// operationIds equal the generated SDK fn names. Centralized here (and reused by useModelFitSchedulerEvents) so the
// literal `_id` key — which trips biome's naming-convention rule — is constructed in exactly one place.
export const modelFitQueryIds = {
	latest: "getLatestRecommendations",
	hardwareProfile: "getHardwareProfile",
	runningModels: "listRunningModels",
	llamaCppVersion: "getLlamaCppVersion",
	hfTokenStatus: "getHfTokenStatus",
	ggufBrowse: "browseGgufRepositories",
	ggufInspect: "inspectGgufRepository",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one model-fit endpoint. */
export function modelFitInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

export function useLatestRecommendations(filters: ModelFitRecommendationFilters) {
	return useQuery({
		...withResponseValidation(
			getLatestRecommendationsOptions({
				query: { useCase: filters.useCase },
			}),
		),
		select: toLatestRecommendations,
	});
}

// The node hardware profile (RAM / VRAM / GPU vendor / CPU mode). `refresh:true` re-probes the box rather than
// serving the briefly-cached profile; the page passes it from the explicit "refresh hardware" action.
export function useHardwareProfile(refresh = false) {
	return useQuery({
		...withResponseValidation(getHardwareProfileOptions({ query: { refresh } })),
		select: toHardwareProfile,
	});
}

// Live running-models list backing the eject UI. enabled lets the page mount it lazily (e.g. only when the panel is open).
export function useRunningModels(enabled = true) {
	return useQuery({
		...withResponseValidation(listRunningModelsOptions()),
		select: (data) => (data.items ?? []).map(toRunningModel),
		enabled,
	});
}

// Resolved llama.cpp binary (version / variant / pinned-fallback). DISABLED by default: the backend's GET handler may
// trigger the first multi-hundred-MB prebuilt download as a side effect, so this read must NOT auto-run on page mount.
// The page passes `enabled:true` only after the operator explicitly clicks "Check version" so the (possibly
// download-triggering) probe is operator-initiated, never a mount side effect.
export function useLlamaCppVersion(enabled = false) {
	return useQuery({
		...withResponseValidation(getLlamaCppVersionOptions()),
		select: toLlamaCppVersion,
		enabled,
	});
}

// HF token status — a boolean only. The token value itself is write-only and never returned by the API.
export function useHfTokenStatus() {
	return useQuery({
		...withResponseValidation(getHfTokenStatusOptions()),
		select: (data) => data.hasToken ?? false,
	});
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

function invalidate(queryClient: ReturnType<typeof useQueryClient>, operationId: string): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: modelFitInvalidationKey(operationId) });
}

// Variables for a refresh: the existing model-recommendation-check job id, plus optional per-run overrides. When
// `useCase` is supplied the scheduler fires that job with a per-fire use-case override (validated server-side against
// the fixed six-value allowlist) so the run targets the use case the operator is viewing. `limit` widens the breadth.
export interface RefreshRecommendationsVariables {
	scheduledJobId: string;
	useCase?: ModelFitUseCase;
	limit?: number;
	quantOverride?: string;
	ctxTarget?: number;
}

// Refresh enqueues an async scheduler run, so it invalidates the latest-recommendations cache (the run may not have
// produced new data yet — useModelFitSchedulerEvents refetches again on completion). The page-facing variables stay
// domain-shaped; the hook dispatches them to the generated mutationFn's `{ body }` shape so callers never touch the wire.
export function useRefreshRecommendations() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (variables: RefreshRecommendationsVariables): Promise<void> => {
			const options = withResponseValidation(refreshRecommendationsMutation());
			await options.mutationFn?.({ body: { ...variables } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, modelFitQueryIds.latest),
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

// Starts a resumable GGUF download via the Hugging Face GGUF model store. On success the running-models list may change as the download begins,
// so invalidate it (the page surfaces the in-flight download from that list). Returns the wire response so the caller
// can read `alreadyInFlight` / the resolved `modelName`.
export function useStartGgufDownload() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (variables: StartGgufDownloadVariables) => {
			const options = withResponseValidation(startGgufDownloadMutation());
			return await options.mutationFn?.({ body: { ...variables } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, modelFitQueryIds.runningModels),
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
		onSuccess: () => invalidate(queryClient, modelFitQueryIds.runningModels),
	});
}

export interface EjectRunningModelVariables {
	modelName: string;
	role?: string;
}

// Ejects a running model from the llama.cpp runtime. Invalidates the running-models list so the ejected entry disappears.
export function useEjectRunningModel() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (variables: EjectRunningModelVariables) => {
			const options = withResponseValidation(ejectRunningModelMutation());
			return await options.mutationFn?.({ body: { ...variables } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, modelFitQueryIds.runningModels),
	});
}

// Ensures (selects/downloads) a llama.cpp binary variant via the llama.cpp runtime. Invalidates the version query so the panel
// reflects the newly active binary.
export function useEnsureLlamaCppBinary() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (variant: LlamaCppVariant) => {
			const options = withResponseValidation(ensureLlamaCppBinaryMutation());
			return await options.mutationFn?.({ body: { variant } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, modelFitQueryIds.llamaCppVersion),
	});
}

// Stores (or clears) the HF token. The token is write-only: passing an empty/omitted token clears it. Invalidates the
// token-status query so the masked form reflects "configured" / "none" without ever reading the value back.
export function useSetHfToken() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (token: string | undefined) => {
			const options = withResponseValidation(setHfTokenMutation());
			// An empty string clears the token server-side; coalesce undefined to null so the body carries an explicit value.
			await options.mutationFn?.({ body: { token: token && token.length > 0 ? token : null } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, modelFitQueryIds.hfTokenStatus),
	});
}

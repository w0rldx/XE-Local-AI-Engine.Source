import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	browseGgufRepositoriesOptions,
	cancelGgufDownloadMutation,
	inspectGgufRepositoryOptions,
	startGgufDownloadMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toGgufRepository, toGgufRepositoryDetail } from "@/features/models/models/GgufMappers";

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
		onSuccess: () => invalidate(queryClient, ggufQueryIds.runningModels),
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
		onSuccess: () => invalidate(queryClient, ggufQueryIds.runningModels),
	});
}

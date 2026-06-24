import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	ensureLlamaCppBinaryMutation,
	getHfTokenStatusOptions,
	getLlamaCppRuntimeOptions,
	setHfTokenMutation,
	updateLlamaCppRuntimeMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toLlamaCppRuntimeStatus } from "@/features/node-settings/models/LocalRuntimeMappers";
import type { LlamaCppVariant } from "@/features/node-settings/models/LocalRuntimeModels";

// Server state for the local-runtime cards on the Node Settings page (relocated from the model-fit advisor — they
// tune this worker's local runtime, not model recommendations). Reads use the generated hey-api `*Options()` (which
// wire the shared axios instance + TanStack Query AbortSignal automatically) wrapped in withResponseValidation so a
// zod response-shape failure surfaces as an ApiError. Mutations adapt the domain variables to the generated `{ body }`
// envelope and invalidate the caches they affect; the HF token is write-only — only its boolean status is fetched.

// The generated query keys are single-element arrays `[{ _id: "<operationId>", ... }]`. Invalidating with just the
// `_id` partial object matches every cached variant of that endpoint. Centralized here so the literal `_id` key —
// which trips biome's naming-convention rule — is constructed in exactly one place.
const localRuntimeQueryIds = {
	llamaCppRuntime: "getLlamaCppRuntime",
	hfTokenStatus: "getHfTokenStatus",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one local-runtime endpoint. */
function localRuntimeInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

function invalidate(queryClient: ReturnType<typeof useQueryClient>, operationId: string): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: localRuntimeInvalidationKey(operationId) });
}

// Ensures (selects/downloads) a llama.cpp binary variant. Invalidates the runtime-status query so the merged card
// reflects the newly active binary's installed tag + variant (the backend records the resolved binary on ensure).
export function useEnsureLlamaCppBinary() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (variant: LlamaCppVariant) => {
			const options = withResponseValidation(ensureLlamaCppBinaryMutation());
			return await options.mutationFn?.({ body: { variant } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, localRuntimeQueryIds.llamaCppRuntime),
	});
}

// Read-only llama.cpp runtime status (installed tag/variant + recommended tag + update-available flag). This GET does
// NOT trigger a binary download, so it is safe to auto-run on mount — the page and the global banner both subscribe to
// it. The mount read is cheap (no `refresh`): it returns the cached recommended/upstream snapshot. A manual recheck is
// driven separately by `useRefreshLlamaCppRuntime`, which hits `?refresh=true` and seeds this query's cache so the
// subscribed panel re-renders. The mapper coalesces the optional generated fields into the domain shape.
export function useLlamaCppRuntimeStatus(enabled = true) {
	return useQuery({
		...withResponseValidation(getLlamaCppRuntimeOptions()),
		select: toLlamaCppRuntimeStatus,
		enabled,
	});
}

// Manual recheck of the llama.cpp runtime status. The mount query (`useLlamaCppRuntimeStatus`) intentionally omits
// `refresh` so the page load stays cheap; this hook fetches the `?refresh=true` variant (which re-resolves recommended
// + upstream-latest against the GitHub release API, behind the backend's 60s rate-limit guard) and writes the fresh
// result into the mount query's cache so every subscriber reflects it without a second mount-key network read.
export function useRefreshLlamaCppRuntime() {
	const queryClient = useQueryClient();

	return async (): Promise<void> => {
		const refreshOptions = withResponseValidation(getLlamaCppRuntimeOptions({ query: { refresh: true } }));
		const fresh = await queryClient.fetchQuery(refreshOptions);
		// Seed the mount-key cache (no `query`) so the panel + banner — which subscribe to the cheap mount read — update
		// in place. The mount query key is the no-param variant; the refresh fetch lives under a distinct key.
		queryClient.setQueryData(getLlamaCppRuntimeOptions().queryKey, fresh);
	};
}

// Installs/updates the llama.cpp runtime to the chosen tag (recommended by default; upstream-latest only under
// developer mode). Adapts the domain variables to the generated `{ body }` envelope and invalidates the runtime-status
// query on success so the panel + banner reflect the freshly installed tag. The caller surfaces the progress/success/
// error toasts (mirrors the model-pull pattern) so this hook stays a thin data-layer seam.
export function useUpdateLlamaCppRuntime() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (variables: { tag: string; variant?: LlamaCppVariant }) => {
			const options = withResponseValidation(updateLlamaCppRuntimeMutation());
			return await options.mutationFn?.({ body: { tag: variables.tag, variant: variables.variant ?? null } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, localRuntimeQueryIds.llamaCppRuntime),
	});
}

// HF token status — a boolean only. The token value itself is write-only and never returned by the API.
export function useHfTokenStatus() {
	return useQuery({
		...withResponseValidation(getHfTokenStatusOptions()),
		select: (data) => data.hasToken ?? false,
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
		onSuccess: () => invalidate(queryClient, localRuntimeQueryIds.hfTokenStatus),
	});
}

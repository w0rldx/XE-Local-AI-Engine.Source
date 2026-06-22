import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	ensureLlamaCppBinaryMutation,
	getHfTokenStatusOptions,
	getLlamaCppVersionOptions,
	setHfTokenMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toLlamaCppVersion } from "@/features/node-settings/models/LocalRuntimeMappers";
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
	llamaCppVersion: "getLlamaCppVersion",
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

// Ensures (selects/downloads) a llama.cpp binary variant. Invalidates the version query so the panel reflects the
// newly active binary.
export function useEnsureLlamaCppBinary() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (variant: LlamaCppVariant) => {
			const options = withResponseValidation(ensureLlamaCppBinaryMutation());
			return await options.mutationFn?.({ body: { variant } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, localRuntimeQueryIds.llamaCppVersion),
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

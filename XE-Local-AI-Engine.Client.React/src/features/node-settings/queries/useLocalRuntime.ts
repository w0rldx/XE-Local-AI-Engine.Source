import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	// Aliased: the generated name carries a hook prefix, but it is a plain options factory called inside mutationFn.
	useAutomaticLlamaCppVariantMutation as automaticLlamaCppVariantMutation,
	cancelLlamaCppSourceBuildMutation,
	ensureLlamaCppBinaryMutation,
	getHardwareProfileOptions,
	getHfTokenStatusOptions,
	getLlamaCppRuntimeOptions,
	getLlamaCppSourceBuildPrerequisitesOptions,
	getLlamaCppSourceBuildStatusOptions,
	removeLlamaCppSourceBuildMutation,
	setHfTokenMutation,
	startLlamaCppSourceBuildMutation,
	updateLlamaCppRuntimeMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { toLlamaCppRuntimeStatus } from "@/features/node-settings/models/LocalRuntimeMappers";
import type { LlamaCppVariant } from "@/features/node-settings/models/LocalRuntimeModels";
import { toSourceBuildPrerequisites, toSourceBuildStatus } from "@/features/node-settings/models/SourceBuildMappers";
import type { LlamaCppSourceBackend, SourceBuildDraft } from "@/features/node-settings/models/SourceBuildModels";
import { sourceBuildPrerequisiteStaleTime, sourceBuildRequest } from "@/features/node-settings/models/SourceBuildModels";

// Server state for the local-runtime cards on the Node Settings page (relocated from the model-fit advisor — they
// tune this worker's local runtime, not model recommendations). Reads use the generated hey-api `*Options()` (which
// wire the shared axios instance + TanStack Query AbortSignal automatically). Mutations adapt the domain variables to the generated `{ body }`
// envelope and invalidate the caches they affect; the HF token is write-only — only its boolean status is fetched.

// Generated keys are object arrays; TanStack partial matching on `_id` invalidates every endpoint variant.
export const localRuntimeQueryIds = {
	llamaCppRuntime: "getLlamaCppRuntime",
	hfTokenStatus: "getHfTokenStatus",
	sourceBuildPrerequisites: "getLlamaCppSourceBuildPrerequisites",
	sourceBuildStatus: "getLlamaCppSourceBuildStatus",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one local-runtime endpoint. */
export function localRuntimeInvalidationKey(operationId: string): readonly [{ _id: string }] {
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
			const options = ensureLlamaCppBinaryMutation();
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
		...getLlamaCppRuntimeOptions(),
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
		const refreshOptions = getLlamaCppRuntimeOptions({ query: { refresh: true } });
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
/**
 * The source-build backend this node's GPU vendor points at: nvidia → cuda, amd/intel → vulkan, anything else (or
 * an unanswered profile) → cpu. Reads the shared cached hardware profile; no probe of its own.
 */
export function useDefaultSourceBuildBackend(): LlamaCppSourceBackend {
	const { data } = useQuery({
		...getHardwareProfileOptions({ query: { refresh: false } }),
		select: (profile) => profile.gpuVendor,
	});
	if (data === "nvidia") {
		return "cuda";
	}
	return data === "amd" || data === "intel" ? "vulkan" : "cpu";
}

export function useUpdateLlamaCppRuntime() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (variables: { tag: string; variant?: LlamaCppVariant }) => {
			const options = updateLlamaCppRuntimeMutation();
			return await options.mutationFn?.({ body: { tag: variables.tag, variant: variables.variant ?? null } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, localRuntimeQueryIds.llamaCppRuntime),
	});
}

// Drops the operator's variant pin so the node chooses the variant automatically again. A 409 (a bring-your-own override
// is active) rejects with the server's message; the caller toasts both outcomes.
export function useRestoreAutomaticLlamaCppVariant() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async () => {
			const options = automaticLlamaCppVariantMutation();
			return await options.mutationFn?.({}, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, localRuntimeQueryIds.llamaCppRuntime),
	});
}

// HF token status — a boolean only. The token value itself is write-only and never returned by the API.
export function useHfTokenStatus() {
	return useQuery({
		...getHfTokenStatusOptions(),
		select: (data) => data.hasToken ?? false,
	});
}

// Stores (or clears) the HF token. The token is write-only: passing an empty/omitted token clears it. Invalidates the
// token-status query so the masked form reflects "configured" / "none" without ever reading the value back.
export function useSetHfToken() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (token: string | undefined) => {
			const options = setHfTokenMutation();
			// An empty string clears the token server-side; coalesce undefined to null so the body carries an explicit value.
			await options.mutationFn?.({ body: { token: token && token.length > 0 ? token : null } }, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, localRuntimeQueryIds.hfTokenStatus),
	});
}

export function useSourceBuildPrerequisites(backend: LlamaCppSourceBackend, enabled = true) {
	return useQuery({
		...getLlamaCppSourceBuildPrerequisitesOptions({ query: { backend } }),
		select: toSourceBuildPrerequisites,
		enabled,
		staleTime: sourceBuildPrerequisiteStaleTime,
	});
}

export function useSourceBuildStatus(enabled = true) {
	return useQuery({
		...getLlamaCppSourceBuildStatusOptions(),
		select: toSourceBuildStatus,
		enabled,
	});
}

export function useStartSourceBuild() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (draft: SourceBuildDraft) => {
			const options = startLlamaCppSourceBuildMutation();
			return await options.mutationFn?.({ body: sourceBuildRequest(draft) }, undefined as never);
		},
		onSuccess: () =>
			Promise.all([
				invalidate(queryClient, localRuntimeQueryIds.sourceBuildStatus),
				invalidate(queryClient, localRuntimeQueryIds.llamaCppRuntime),
			]),
	});
}

export function useCancelSourceBuild() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async () => {
			const options = cancelLlamaCppSourceBuildMutation();
			return await options.mutationFn?.({}, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, localRuntimeQueryIds.sourceBuildStatus),
	});
}

export function useRemoveSourceBuild() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async () => {
			const options = removeLlamaCppSourceBuildMutation();
			return await options.mutationFn?.({}, undefined as never);
		},
		onSuccess: () =>
			Promise.all([
				invalidate(queryClient, localRuntimeQueryIds.sourceBuildStatus),
				invalidate(queryClient, localRuntimeQueryIds.llamaCppRuntime),
			]),
	});
}

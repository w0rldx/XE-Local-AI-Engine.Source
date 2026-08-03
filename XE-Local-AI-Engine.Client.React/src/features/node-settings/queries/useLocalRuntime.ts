import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import type { XeLocalAiEngineClientEndpointsModelFitV1RuntimeAcquisitionStatusResponse } from "@/core/api/generated";
import {
	cancelCudaBuildMutation,
	cancelLlamaCppSourceBuildMutation,
	ensureLlamaCppBinaryMutation,
	getCudaBuildPrerequisitesOptions,
	getCudaBuildStatusOptions,
	getHfTokenStatusOptions,
	getLlamaCppRuntimeOptions,
	getLlamaCppSourceBuildPrerequisitesOptions,
	getLlamaCppSourceBuildStatusOptions,
	getRuntimeAcquisitionStatusOptions,
	removeCudaBuildMutation,
	removeLlamaCppSourceBuildMutation,
	setHfTokenMutation,
	startCudaBuildMutation,
	startLlamaCppSourceBuildMutation,
	updateLlamaCppRuntimeMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import {
	toCudaBuildPrerequisites,
	toCudaBuildStatus,
	toLlamaCppRuntimeStatus,
} from "@/features/node-settings/models/LocalRuntimeMappers";
import type { LlamaCppVariant } from "@/features/node-settings/models/LocalRuntimeModels";
import { toSourceBuildPrerequisites, toSourceBuildStatus } from "@/features/node-settings/models/SourceBuildMappers";
import type { LlamaCppSourceBackend, SourceBuildDraft } from "@/features/node-settings/models/SourceBuildModels";
import { sourceBuildRequest } from "@/features/node-settings/models/SourceBuildModels";

// Server state for the local-runtime cards on the Node Settings page (relocated from the model-fit advisor — they
// tune this worker's local runtime, not model recommendations). Reads use the generated hey-api `*Options()` (which
// wire the shared axios instance + TanStack Query AbortSignal automatically) wrapped in withResponseValidation so a
// zod response-shape failure surfaces as an ApiError. Mutations adapt the domain variables to the generated `{ body }`
// envelope and invalidate the caches they affect; the HF token is write-only — only its boolean status is fetched.

// The generated query keys are single-element arrays `[{ _id: "<operationId>", ... }]`. Invalidating with just the
// `_id` partial object matches every cached variant of that endpoint. Centralized here so the literal `_id` key —
// which trips biome's naming-convention rule — is constructed in exactly one place.
export const localRuntimeQueryIds = {
	llamaCppRuntime: "getLlamaCppRuntime",
	hfTokenStatus: "getHfTokenStatus",
	cudaBuildPrerequisites: "getCudaBuildPrerequisites",
	cudaBuildStatus: "getCudaBuildStatus",
	sourceBuildPrerequisites: "getLlamaCppSourceBuildPrerequisites",
	sourceBuildStatus: "getLlamaCppSourceBuildStatus",
	runtimeAcquisitionStatus: "getRuntimeAcquisitionStatus",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one local-runtime endpoint. */
export function localRuntimeInvalidationKey(operationId: string): readonly [{ _id: string }] {
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

/** The llama.cpp runtime-acquisition snapshot, exactly as it travels on both the hydrate GET and the hub push. */
export type RuntimeAcquisitionStatus = XeLocalAiEngineClientEndpointsModelFitV1RuntimeAcquisitionStatusResponse;

/**
 * The monotonic-sequence guard, and the ONLY rule by which the acquisition cache entry may advance.
 *
 * The hydrate GET and the hub push carry the same snapshot but travel different paths, so they race in BOTH
 * directions: a GET issued just before a terminal transition can *arrive* after the corresponding push. Taking the
 * later arrival would overwrite `Completed`/`Failed` with a stale `Downloading` and leave the banner running forever
 * with nothing left to end it. The backend therefore stamps every status write with a strictly increasing `sequence`,
 * and both writers funnel through here: an update is accepted only when its sequence beats what is already cached.
 * Timestamps would not do — the two paths have no common clock and no ordering relative to each other.
 */
export function keepLatestAcquisitionStatus(
	current: RuntimeAcquisitionStatus | undefined,
	incoming: RuntimeAcquisitionStatus,
): RuntimeAcquisitionStatus {
	return current !== undefined && current.sequence >= incoming.sequence ? current : incoming;
}

// Read-only llama.cpp runtime-acquisition status (GET model-fit/llamacpp/acquisition) — the hydrate leg of the
// first-run banner. Like the runtime-status GET above this is side-effect free: it reports what the host is already
// doing and never starts an acquisition (unlike the `ensure` POST), so it is safe to auto-run on mount. That mount
// read is what covers the late-join case the banner exists for: the runtime download starts within seconds of boot,
// typically before the client has authenticated and opened its hub connection, so a push-only channel would show the
// user nothing at all for precisely the slow first run this surfaces.
//
// `structuralSharing` is the choke-point for the sequence guard: TanStack routes BOTH this query's own fetch result
// and every `setQueryData` from the hub through it, so the stale-hydrate-after-terminal-push race cannot be won by
// arrival order no matter which leg lands last.
export function useRuntimeAcquisitionStatus(enabled = true) {
	return useQuery({
		...withResponseValidation(getRuntimeAcquisitionStatusOptions()),
		structuralSharing: (current, incoming) =>
			keepLatestAcquisitionStatus(current as RuntimeAcquisitionStatus | undefined, incoming as RuntimeAcquisitionStatus),
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

// Read-only CUDA build prerequisite report (GET cuda-build/prerequisites). Safe to auto-run on mount — it only probes
// host capabilities (Linux, nvcc, cmake, NVIDIA GPU, disk) and returns the checklist + the authoritative `canBuild`
// gate. The card derives both its Linux gate and the "Build CUDA" enablement from this query.
export function useCudaBuildPrerequisites(enabled = true) {
	return useQuery({
		...withResponseValidation(getCudaBuildPrerequisitesOptions()),
		select: toCudaBuildPrerequisites,
		enabled,
	});
}

// Read-only CUDA build status (GET cuda-build/status). Recovers an in-progress build after a client reconnect: a page
// reload mid-build re-reads the phase + accumulated log here, then the live SignalR hub appends subsequent deltas.
export function useCudaBuildStatus(enabled = true) {
	return useQuery({
		...withResponseValidation(getCudaBuildStatusOptions()),
		select: toCudaBuildStatus,
		enabled,
	});
}

// Starts the in-app CUDA build (POST cuda-build). The build runs server-side (background), so this mutation only kicks
// it off; live progress arrives via the SignalR hub and the status query. Invalidates the status query so `isRunning`
// flips true, and the runtime status so a managed-build adoption is reflected. A 409 (eject-first / already building /
// disk) is surfaced by the caller from the thrown AxiosError's response body.
export function useStartCudaBuild() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async () => {
			const options = withResponseValidation(startCudaBuildMutation());
			return await options.mutationFn?.({}, undefined as never);
		},
		onSuccess: () =>
			Promise.all([
				invalidate(queryClient, localRuntimeQueryIds.cudaBuildStatus),
				invalidate(queryClient, localRuntimeQueryIds.llamaCppRuntime),
			]),
	});
}

// Cancels a running CUDA build (POST cuda-build/cancel). The backend tree-kills the build process group and records a
// terminal `cancelled` status; invalidate the status query (and the runtime, which may revert to a prior runtime).
export function useCancelCudaBuild() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async () => {
			const options = withResponseValidation(cancelCudaBuildMutation());
			return await options.mutationFn?.({}, undefined as never);
		},
		onSuccess: () =>
			Promise.all([
				invalidate(queryClient, localRuntimeQueryIds.cudaBuildStatus),
				invalidate(queryClient, localRuntimeQueryIds.llamaCppRuntime),
			]),
	});
}

// Removes the managed CUDA source build (POST cuda-build/remove). Deletes the built tree (server-side path-guarded) and
// returns the post-removal runtime status; invalidate the runtime so the card reverts to the prebuilt/download path, and
// the status query. A 409 (eject-first) is surfaced by the caller from the thrown AxiosError's response body.
export function useRemoveCudaBuild() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async () => {
			const options = withResponseValidation(removeCudaBuildMutation());
			return await options.mutationFn?.({}, undefined as never);
		},
		onSuccess: () =>
			Promise.all([
				invalidate(queryClient, localRuntimeQueryIds.llamaCppRuntime),
				invalidate(queryClient, localRuntimeQueryIds.cudaBuildStatus),
			]),
	});
}

export function useSourceBuildPrerequisites(backend: LlamaCppSourceBackend, enabled = true) {
	return useQuery({
		...withResponseValidation(getLlamaCppSourceBuildPrerequisitesOptions({ query: { backend } })),
		select: toSourceBuildPrerequisites,
		enabled,
	});
}

export function useSourceBuildStatus(enabled = true) {
	return useQuery({
		...withResponseValidation(getLlamaCppSourceBuildStatusOptions()),
		select: toSourceBuildStatus,
		enabled,
	});
}

export function useStartSourceBuild() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async (draft: SourceBuildDraft) => {
			const options = withResponseValidation(startLlamaCppSourceBuildMutation());
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
			const options = withResponseValidation(cancelLlamaCppSourceBuildMutation());
			return await options.mutationFn?.({}, undefined as never);
		},
		onSuccess: () => invalidate(queryClient, localRuntimeQueryIds.sourceBuildStatus),
	});
}

export function useRemoveSourceBuild() {
	const queryClient = useQueryClient();
	return useMutation({
		mutationFn: async () => {
			const options = withResponseValidation(removeLlamaCppSourceBuildMutation());
			return await options.mutationFn?.({}, undefined as never);
		},
		onSuccess: () =>
			Promise.all([
				invalidate(queryClient, localRuntimeQueryIds.sourceBuildStatus),
				invalidate(queryClient, localRuntimeQueryIds.llamaCppRuntime),
			]),
	});
}

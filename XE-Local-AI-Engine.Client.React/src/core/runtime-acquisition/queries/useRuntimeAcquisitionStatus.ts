import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import type { XeLocalAiEngineClientEndpointsModelFitV1RuntimeAcquisitionStatusResponse } from "@/core/api/generated";
import { ensureLlamaCppBinaryMutation, getRuntimeAcquisitionStatusOptions } from "@/core/api/generated/@tanstack/react-query.gen";

/** The llama.cpp runtime-acquisition snapshot, exactly as it travels on both the hydrate GET and the hub push. */
export type RuntimeAcquisitionStatus = XeLocalAiEngineClientEndpointsModelFitV1RuntimeAcquisitionStatusResponse;

/** Partial generated-query-key filter matching every cached variant of the llama.cpp runtime-status read. */
export const llamaCppRuntimeInvalidationKey = [{ _id: "getLlamaCppRuntime" }] as const;

const RETRY_VARIANTS = ["cpu", "cuda", "vulkan"] as const;

/**
 * The monotonic-sequence guard, and the ONLY rule by which the acquisition cache entry may advance.
 *
 * The hydrate GET and the hub push carry the same snapshot but travel different paths, so they race in BOTH
 * directions: a GET issued just before a terminal transition can *arrive* after the corresponding push. Taking the
 * later arrival would overwrite `Completed`/`Failed` with a stale `Downloading` and leave the operation running forever
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
// first-run operation. Side-effect free: it reports what the host is already doing and never starts an acquisition
// (unlike the `ensure` POST), so it is safe to auto-run on mount. That mount read covers the late-join case: the
// runtime download starts within seconds of boot, typically before the client has authenticated and opened its hub
// connection, so a push-only channel would show the user nothing at all for precisely the slow first run.
//
// `structuralSharing` is the choke-point for the sequence guard: TanStack routes BOTH this query's own fetch result
// and every `setQueryData` from the hub through it, so the stale-hydrate-after-terminal-push race cannot be won by
// arrival order no matter which leg lands last.
export function useRuntimeAcquisitionStatus(enabled = true) {
	return useQuery({
		...getRuntimeAcquisitionStatusOptions(),
		structuralSharing: (current, incoming) =>
			keepLatestAcquisitionStatus(current as RuntimeAcquisitionStatus | undefined, incoming as RuntimeAcquisitionStatus),
		enabled,
	});
}

/**
 * The variant to re-ensure after a failed acquisition. The status carries the server's enum name ("Cuda"), the ensure
 * route the lower-case variant; a probe that failed before choosing one leaves `variant` null, and cpu is the
 * always-available fallback.
 */
function retryAcquisitionVariant(status: RuntimeAcquisitionStatus): string {
	const reported = status.variant?.toLowerCase();
	return RETRY_VARIANTS.find((variant) => variant === reported) ?? "cpu";
}

/** Re-ensures the failed acquisition's variant; the new acquisition then arrives over the hub like the first one. */
export function useRetryRuntimeAcquisition() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (status: RuntimeAcquisitionStatus) => {
			const options = ensureLlamaCppBinaryMutation();
			return await options.mutationFn?.({ body: { variant: retryAcquisitionVariant(status) } }, undefined as never);
		},
		onSuccess: () => queryClient.invalidateQueries({ queryKey: llamaCppRuntimeInvalidationKey }),
	});
}

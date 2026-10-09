import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { ejectRunningModelMutation, listRunningModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useRuntimeResidencyHub } from "@/core/api/signalr/useRuntimeResidencyHub";
import {
	type EjectRunningModelResult,
	toEjectRunningModelResult,
	toRunningModel,
} from "@/features/loaded-models/models/RunningModelsModels";

// Server state for the llama.cpp running-models section on the Loaded Models page (relocated from the model-fit
// advisor): it lists llama.cpp server processes. Reads use the generated hey-api `*Options()` (which wire the shared axios instance + TanStack Query
// AbortSignal automatically) wrapped in withResponseValidation so a zod response-shape failure surfaces as an
// ApiError. The eject mutation invalidates the running-models list so the ejected entry disappears.

// Generated keys are object arrays; TanStack partial matching on `_id` invalidates every endpoint variant.
const runningModelsOperationId = "listRunningModels";

// The llama.cpp runtime status carries `runningProcessCount`, which the Runtimes page reads; nothing else refreshes it
// after an eject, so the page kept saying models were still loaded.
const llamaCppRuntimeOperationId = "getLlamaCppRuntime";

/** Builds the partial generated-query-key filter that matches every cached variant of the running-models endpoint. */
function runningModelsInvalidationKey(): readonly [{ _id: string }] {
	return [{ _id: runningModelsOperationId }];
}

// Fallback poll cadence (ms). llama.cpp server processes appear as chat sends warm models and disappear via idle-TTL
// eviction or graceful ejects — none of which flow through a REST mutation a page could hang an invalidation on. The
// runtime-residency hub pushes those changes; while it is degraded the list polls at this cadence instead. No
// unavailable back-off is needed because this endpoint reads the app's own in-process supervisor, never an optional
// external daemon.
export const runningModelsPollIntervalMs = 4000;

// Safety net while the hub is live, for a change no server-side raise point announced.
export const runningModelsPushFloorMs = 60_000;

// Live running-models list backing the eject UI, kept current by the runtime-residency hub. enabled lets the page mount
// it lazily (e.g. only when the section is shown); a disabled query neither polls nor connects the hub.
export function useRunningModels(enabled = true) {
	const { isLive } = useRuntimeResidencyHub(enabled, runningModelsInvalidationKey());
	return useQuery({
		...withResponseValidation(listRunningModelsOptions()),
		select: (data) => (data.items ?? []).map(toRunningModel),
		enabled,
		// Hub live: the push floor only. Otherwise (b): the fallback cadence while the hub is degraded or not started.
		refetchInterval: isLive ? runningModelsPushFloorMs : runningModelsPollIntervalMs,
	});
}

export interface EjectRunningModelVariables {
	modelName: string;
	role?: string;
	// When true, tear the process down even if in-flight inference has not drained within the bounded window
	// (interrupting the running turn). Defaults to false (graceful — never interrupts a running turn).
	force?: boolean;
}

// Ejects a running model from the llama.cpp runtime, returning what the eject actually did (ejected /
// timed_out_still_busy / forced / not_running) so the page can surface a distinct outcome toast. Invalidates the
// running-models list so an ejected entry disappears, and the llama.cpp runtime status so its process count follows.
export function useEjectRunningModel() {
	const queryClient = useQueryClient();

	return useMutation<EjectRunningModelResult, Error, EjectRunningModelVariables>({
		mutationFn: async (variables: EjectRunningModelVariables) => {
			const options = withResponseValidation(ejectRunningModelMutation());
			const response = await options.mutationFn?.({ body: { ...variables } }, undefined as never);
			return toEjectRunningModelResult(response);
		},
		onSuccess: () =>
			Promise.all([
				queryClient.invalidateQueries({ queryKey: runningModelsInvalidationKey() }),
				queryClient.invalidateQueries({ queryKey: [{ _id: llamaCppRuntimeOperationId }] }),
			]),
	});
}

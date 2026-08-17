import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { ejectRunningModelMutation, listRunningModelsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { type EjectRunningModelResult, toEjectRunningModelResult, toRunningModel } from "@/features/loaded-models/models/RunningModelsModels";

// Server state for the llama.cpp running-models section on the Loaded Models page (relocated from the model-fit
// advisor). This is a DIFFERENT runtime from the Ollama in-memory list (useLoadedModels): it lists llama.cpp server
// processes. Reads use the generated hey-api `*Options()` (which wire the shared axios instance + TanStack Query
// AbortSignal automatically) wrapped in withResponseValidation so a zod response-shape failure surfaces as an
// ApiError. The eject mutation invalidates the running-models list so the ejected entry disappears.

// The generated query keys are single-element arrays `[{ _id: "<operationId>", ... }]`. Invalidating with just the
// `_id` partial object matches every cached variant of that endpoint. Centralized here (and shared with the GGUF
// download mutations on the Model Management page, which also touch this list) via the literal operationId.
const runningModelsOperationId = "listRunningModels";

/** Builds the partial generated-query-key filter that matches every cached variant of the running-models endpoint. */
function runningModelsInvalidationKey(): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: runningModelsOperationId }];
}

// Poll cadence (ms) while the section is mounted. llama.cpp server processes appear as chat sends warm models and
// disappear via idle-TTL eviction or graceful ejects — none of which flow through a REST mutation this page could hang
// an invalidation on — so without polling the list only refreshes on manual reload. Mirrors the 4s cadence of the
// adjacent Ollama query (useLoadedModels); no unavailable back-off is needed here because this endpoint reads the
// app's own in-process supervisor, never an optional external daemon.
export const runningModelsPollIntervalMs = 4000;

// Live running-models list backing the eject UI. enabled lets the page mount it lazily (e.g. only when the section is
// shown); a disabled query does not poll.
export function useRunningModels(enabled = true) {
	return useQuery({
		...withResponseValidation(listRunningModelsOptions()),
		select: (data) => (data.items ?? []).map(toRunningModel),
		enabled,
		refetchInterval: runningModelsPollIntervalMs,
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
// running-models list so an ejected entry disappears.
export function useEjectRunningModel() {
	const queryClient = useQueryClient();

	return useMutation<EjectRunningModelResult, Error, EjectRunningModelVariables>({
		mutationFn: async (variables: EjectRunningModelVariables) => {
			const options = withResponseValidation(ejectRunningModelMutation());
			const response = await options.mutationFn?.({ body: { ...variables } }, undefined as never);
			return toEjectRunningModelResult(response);
		},
		onSuccess: () => queryClient.invalidateQueries({ queryKey: runningModelsInvalidationKey() }),
	});
}

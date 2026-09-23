import { type QueryKey, useQuery } from "@tanstack/react-query";

import { getRunningLocalModels } from "@/core/api/generated";
import { callWithResponseValidation } from "@/core/api/ResponseValidation";
import { toLoadedModelsSnapshot } from "@/features/loaded-models/models/LoadedModelsMappers";
import type { LoadedModelsSnapshot } from "@/features/loaded-models/models/LoadedModelsModels";

// Server state for the optional Ollama runtime's in-memory model list. The Loaded Models page no longer renders it
// (that page shows the llama.cpp runtime only); the assist feature reads it to prefer an already-warm model. The hook
// calls the generated hey-api SDK fn DIRECTLY through the shared `callWithResponseValidation` bridge — the same
// imperative pattern the chat adapter uses — so a post-2xx zod response-shape failure surfaces as an ApiError, never a
// raw ZodError.

const loadedModelsQueryKey: QueryKey = ["loaded-models", "running"];

// Poll cadence (ms) while a consumer is mounted AND the provider is reachable: the running set changes as the runtime
// loads/evicts models, so a short interval keeps it current. 4s — light enough for a local endpoint.
const loadedModelsPollIntervalMs = 4000;

// Back-off cadence (ms) once the last snapshot reported the provider unreachable. Ollama is an OPTIONAL secondary
// provider that is deliberately absent on the desktop default, so hammering an absent daemon every 4s only spams
// connection-refused traces with no upside. It still recovers within one long interval if Ollama later comes up.
const unavailablePollIntervalMs = 30_000;

/**
 * Chooses the poll cadence from the latest snapshot: STOP polling (`false`) once the node reports the Ollama runtime
 * is not configured at all (nothing will ever answer), the slow back-off cadence while a configured provider is
 * unreachable, and the fast cadence while it is available (or before the first response). Because the
 * `ollamaConfigured` flag is only known AFTER the first response, this rides `refetchInterval` rather than `enabled`
 * (which cannot depend on the query's own data). Exported so the decision is unit-testable without react-query timers.
 */
export function resolveLoadedModelsPollIntervalMs(snapshot: LoadedModelsSnapshot | undefined): number | false {
	if (snapshot?.ollamaConfigured === false) {
		return false;
	}

	return snapshot?.isAvailable === false ? unavailablePollIntervalMs : loadedModelsPollIntervalMs;
}

/**
 * Lists the models the Ollama runtime currently holds in memory. The TanStack `AbortSignal` is wired into the
 * generated request so an unmount/refetch cancels the in-flight GET. The endpoint degrades gracefully (200 +
 * `isAvailable:false`) when the provider is unreachable, so the query resolves to a snapshot rather than erroring.
 */
export function useLoadedModels() {
	return useQuery<LoadedModelsSnapshot>({
		queryKey: loadedModelsQueryKey,
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(getRunningLocalModels({ signal, throwOnError: true }));
			return toLoadedModelsSnapshot(data);
		},
		refetchInterval: (query) => resolveLoadedModelsPollIntervalMs(query.state.data),
	});
}

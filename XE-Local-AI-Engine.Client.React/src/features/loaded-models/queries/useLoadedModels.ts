import { type QueryKey, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import { getRunningLocalModels, unloadLocalModel } from "@/core/api/generated";
import { callWithResponseValidation } from "@/core/api/ResponseValidation";
import { toLoadedModelsSnapshot, toUnloadResult } from "@/features/loaded-models/models/LoadedModelsMappers";
import type { LoadedModelsSnapshot, UnloadResult } from "@/features/loaded-models/models/LoadedModelsModels";

// Server state for the loaded-models surface. Unlike the model-fit feature (which wraps the generated TanStack
// `*Options()`), this hook calls the generated hey-api SDK fn DIRECTLY through the shared `callWithResponseValidation`
// bridge — the same imperative pattern the chat adapter uses — so a post-2xx zod response-shape failure surfaces as an
// ApiError, never a raw ZodError. The list query polls while the page is mounted/focused so the in-memory footprint
// stays live; the unload (eject) mutation optimistically drops the row, then invalidates so the next poll reconciles.

// Local query key for the running-models list. Kept private and shared between the list query and the eject
// mutation's optimistic update + invalidation so the cache key is constructed in exactly one place.
export const loadedModelsQueryKey: QueryKey = ["loaded-models", "running"];

// Poll cadence (ms) while the page is mounted AND the provider is reachable. The running set changes as the runtime
// loads/evicts models and as a model's idle timer (expiresAtUtc) counts down, so a short interval keeps the memory
// view current without manual refresh. 4s — frequent enough to feel live, light enough for a local endpoint.
const loadedModelsPollIntervalMs = 4000;

// Back-off cadence (ms) once the last snapshot reported the provider unreachable. Ollama is an OPTIONAL secondary
// provider that is deliberately absent on the desktop default, so hammering an absent daemon every 4s only spams
// connection-refused traces (each poll re-attempts the unreachable endpoint) with no upside. Slow the poll right
// down while unavailable; it still recovers automatically within one long interval if Ollama later comes up, and
// the separate llama.cpp running-models query keeps refreshing on its own cadence regardless.
const unavailablePollIntervalMs = 30_000;

/**
 * Chooses the poll cadence for the loaded-models query from the latest snapshot: STOP polling (`false`) once the node
 * reports the Ollama runtime is not configured at all (nothing will ever answer — AUD4-20), the slow back-off cadence
 * while a configured provider is unreachable, and the fast cadence while it is available (or before the first
 * response). Because the `ollamaConfigured` flag is only known AFTER the first response, this rides `refetchInterval`
 * (which gates the recurring poll on the fetched data) rather than the query's `enabled` option (which cannot depend on
 * the query's own data). Exported so the decision is unit-testable without driving react-query's internal timers.
 */
export function resolveLoadedModelsPollIntervalMs(snapshot: LoadedModelsSnapshot | undefined): number | false {
	if (snapshot?.ollamaConfigured === false) {
		return false;
	}

	return snapshot?.isAvailable === false ? unavailablePollIntervalMs : loadedModelsPollIntervalMs;
}

/**
 * Lists the models the local runtime currently holds in memory. The TanStack `AbortSignal` is wired into the
 * generated request so an unmount/refetch cancels the in-flight GET. The endpoint degrades gracefully (200 +
 * `isAvailable:false`) when the provider is unreachable, so the query resolves to a snapshot rather than erroring on
 * provider downtime. The poll interval adapts to that snapshot: {@link loadedModelsPollIntervalMs} while reachable,
 * {@link unavailablePollIntervalMs} once unreachable — so a deliberately-absent Ollama isn't polled aggressively.
 */
export function useLoadedModels() {
	return useQuery<LoadedModelsSnapshot>({
		queryKey: loadedModelsQueryKey,
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(getRunningLocalModels({ signal, throwOnError: true }));
			return toLoadedModelsSnapshot(data);
		},
		// Adaptive cadence: back off to a slow poll while the provider is unreachable so an absent Ollama (desktop
		// default) doesn't drive a 4s connection-refused loop; resume the fast poll the moment it reports available.
		refetchInterval: (query) => resolveLoadedModelsPollIntervalMs(query.state.data),
	});
}

/**
 * Gracefully ejects (unloads) a model from RAM/VRAM. The backend sets keep_alive=0 so the runtime evicts the model
 * AFTER any in-flight generation finishes — it never interrupts a running turn. Optimistically removes the row from
 * the cached snapshot for instant feedback, then invalidates so the next poll reflects the runtime's real state
 * (and restores the row if the eviction has not happened yet). Idempotent: unloading a non-loaded model is a no-op.
 */
export function useEjectModel() {
	const queryClient = useQueryClient();

	return useMutation<UnloadResult, Error, string, { previous: LoadedModelsSnapshot | undefined }>({
		mutationFn: async (modelName: string) => {
			const { data } = await callWithResponseValidation(
				// Route-only POST: the model name rides the path and NO body is sent. The generated requestValidator
				// types `body` as `z.never().optional()`, so passing even `{}` fails zod parsing client-side and the
				// request never reaches the wire; the endpoint's `Accepts<UnloadLocalModelRequest>()` override is what
				// keeps a body-less POST (no Content-Type) out of FastEndpoints' 415.
				unloadLocalModel({ path: { modelName }, throwOnError: true }),
			);
			return toUnloadResult(data);
		},
		onMutate: async (modelName) => {
			// Cancel in-flight polls so they do not clobber the optimistic update, then drop the ejected row.
			await queryClient.cancelQueries({ queryKey: loadedModelsQueryKey });
			const previous = queryClient.getQueryData<LoadedModelsSnapshot>(loadedModelsQueryKey);
			if (previous) {
				queryClient.setQueryData<LoadedModelsSnapshot>(loadedModelsQueryKey, {
					...previous,
					models: previous.models.filter((model) => model.modelName !== modelName),
				});
			}
			return { previous };
		},
		onError: (_error, _modelName, context) => {
			// Restore the pre-mutation snapshot so a failed eject does not leave the row missing.
			if (context?.previous) {
				queryClient.setQueryData(loadedModelsQueryKey, context.previous);
			}
		},
		onSettled: () => queryClient.invalidateQueries({ queryKey: loadedModelsQueryKey }),
	});
}

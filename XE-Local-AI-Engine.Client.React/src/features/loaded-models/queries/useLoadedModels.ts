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

// Poll cadence (ms) while the page is mounted. The running set changes as the runtime loads/evicts models and as a
// model's idle timer (expiresAtUtc) counts down, so a short interval keeps the memory view current without manual
// refresh. 4s — frequent enough to feel live, light enough for a local endpoint.
const loadedModelsPollIntervalMs = 4000;

/**
 * Lists the models the local runtime currently holds in memory, polling every {@link loadedModelsPollIntervalMs}.
 * The TanStack `AbortSignal` is wired into the generated request so an unmount/refetch cancels the in-flight GET.
 * The endpoint degrades gracefully (200 + `isAvailable:false`) when the provider is unreachable, so the query
 * resolves to a snapshot rather than erroring on provider downtime.
 */
export function useLoadedModels() {
	return useQuery<LoadedModelsSnapshot>({
		queryKey: loadedModelsQueryKey,
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(getRunningLocalModels({ signal, throwOnError: true }));
			return toLoadedModelsSnapshot(data);
		},
		refetchInterval: loadedModelsPollIntervalMs,
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
				// The unload endpoint is a body-bound POST: FastEndpoints 415s a truly empty route-only POST, so an
				// empty JSON object `{}` must ride the request. The generated `body` type is `never` (the request DTO is
				// `{ [key: string]: never }`), so the empty object is cast through `never` to satisfy the SDK signature.
				unloadLocalModel({ path: { modelName }, body: {} as never, throwOnError: true }),
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

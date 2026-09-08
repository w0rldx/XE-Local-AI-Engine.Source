import { useQuery } from "@tanstack/react-query";

import { getRunningLocalModels } from "@/core/api/generated";
import { callWithResponseValidation } from "@/core/api/ResponseValidation";

/**
 * Reads whether the optional Ollama runtime is configured/enabled on this node at all (the
 * `XE_OLLAMA_RUNTIME_ENABLED` gate). Distinct from reachability: a configured daemon can still be down.
 *
 * Lives in core/ rather than in the loaded-models feature because node-settings needs the same answer, and a
 * feature must not import another feature's query hook (dependency-cruiser's `no-cross-feature` rule). It reads
 * the same running-models endpoint but keeps its own cache entry, so it never collides with the richer snapshot
 * the loaded-models page caches. The gate is a process-lifetime environment flag, so the answer never goes stale
 * while the SPA is open — it is fetched once and reused.
 *
 * Callers must FAIL OPEN: treat only a definite `false` as "disabled", never `undefined` (still loading, or the
 * probe failed), so a transient error can never hide a setting the operator still needs.
 */
export function useOllamaRuntimeConfigured() {
	return useQuery<boolean>({
		queryKey: ["ollama-runtime", "configured"],
		queryFn: async ({ signal }) => {
			const { data } = await callWithResponseValidation(getRunningLocalModels({ signal, throwOnError: true }));
			// Absent on an older backend that predates the flag: assume configured, the pre-flag behavior.
			return data.ollamaConfigured ?? true;
		},
		staleTime: Number.POSITIVE_INFINITY,
	});
}

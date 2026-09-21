import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	deleteAgentHomeRunMutation,
	getAgentHomeRunLogOptions,
	getAgentHomeRunPatchOptions,
	listAgentHomeRunsOptions,
	listAgentHomeRunsQueryKey,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { agentRunPageSize, toAgentRunView } from "@/features/agentRuns/models/AgentRunModels";

interface AgentRunListOptions {
	readonly limit?: number;
	readonly offset?: number;
}

/**
 * One page of the node's AgentHome run history, newest first, plus the unpaged total the pager numbers.
 *
 * `limit`/`offset` ride in the generated query key, so page 2 is its own cache entry with no data of its own —
 * `keepPreviousData` is what stops `totalCount` reading as 0 for a render and bouncing the operator back to page 1
 * while the next page is still in flight, the same reason the integration lists hold their previous page.
 */
export function useAgentRuns(options: AgentRunListOptions = {}) {
	return useQuery({
		...withResponseValidation(
			listAgentHomeRunsOptions({
				query: {
					limit: options.limit ?? agentRunPageSize,
					offset: options.offset ?? 0,
				},
			}),
		),
		placeholderData: keepPreviousData,
		select: (data) => ({ items: data.items.map(toAgentRunView), totalCount: data.totalCount }),
	});
}

/**
 * Removes one run: its log, its commands and its exported patch.
 *
 * Invalidates the whole run-list family rather than one page's key — a removed run renumbers every page after it, so
 * the page the operator is looking at is not the only one whose contents changed. The node refuses with a 409 while a
 * run holds the execution lease; the caller shows that refusal rather than retrying.
 */
export function useDeleteAgentHomeRun() {
	const queryClient = useQueryClient();
	return useMutation({
		...deleteAgentHomeRunMutation(),
		onSuccess: () => queryClient.invalidateQueries({ queryKey: listAgentHomeRunsQueryKey() }),
	});
}

/**
 * One run's event log, or its exported patch, as capped text.
 *
 * `enabled` is the whole point of the flag: the viewer is mounted by the page and both tabs live inside it, so
 * without it opening a dialog would fetch a patch nobody asked to see, and a closed dialog would keep both files
 * warm in the cache.
 */
export function useAgentRunLog(runId: string | null, enabled: boolean) {
	return useQuery({
		...withResponseValidation(getAgentHomeRunLogOptions({ path: { runId: runId ?? "" } })),
		enabled: enabled && runId !== null,
	});
}

/** The run's exported `changes.patch` as text, under the same open-only rule as the log. */
export function useAgentRunPatch(runId: string | null, enabled: boolean) {
	return useQuery({
		...withResponseValidation(getAgentHomeRunPatchOptions({ path: { runId: runId ?? "" } })),
		enabled: enabled && runId !== null,
	});
}

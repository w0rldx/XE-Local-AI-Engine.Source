import { keepPreviousData, useQuery } from "@tanstack/react-query";

import { listAgentHomeRunsOptions } from "@/core/api/generated/@tanstack/react-query.gen";
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

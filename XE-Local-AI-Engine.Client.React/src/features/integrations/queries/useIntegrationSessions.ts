import { keepPreviousData, useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	deleteIntegrationSessionMutation,
	listIntegrationSessionsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toIntegrationSession } from "@/features/integrations/models/IntegrationMappers";
import { type IntegrationSessionFilters, integrationPageSize } from "@/features/integrations/models/IntegrationModels";
import { integrationInvalidationKey, integrationQueryIds } from "@/features/integrations/queries/useIntegrationTriggers";

// Server state for integration sessions. Both filters AND the paging are server-side for the same reason: the list is
// one page of the server's ordering, so narrowing or slicing it in the browser would hide sessions that match the
// filter but fall on another page.

/** One page of sessions plus the count of rows the same filters match, which is what the pager numbers. */
export function useIntegrationSessions(
	filters: IntegrationSessionFilters = {},
	options: { readonly limit?: number; readonly offset?: number } = {},
) {
	return useQuery({
		...withResponseValidation(
			listIntegrationSessionsOptions({
				query: {
					...(filters.triggerId === undefined ? {} : { triggerId: filters.triggerId }),
					...(filters.status === undefined ? {} : { status: filters.status }),
					limit: options.limit ?? integrationPageSize,
					offset: options.offset ?? 0,
				},
			}),
		),
		// The page bound is part of the query key, so page 2 starts empty; holding the previous page keeps `totalCount`
		// non-zero and stops the pager's clamp from bouncing back to page 1 mid-request.
		placeholderData: keepPreviousData,
		select: (data) => ({ items: data.items.map(toIntegrationSession), totalCount: data.totalCount }),
	});
}

/**
 * Deleting a session purges the owned conversation and, with it, the session's executions and their events. The
 * backend refuses with 409 while an execution on the session is still Accepted/Queued/Running — cancel it first.
 */
export function useDeleteIntegrationSession() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(deleteIntegrationSessionMutation()),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: integrationInvalidationKey(integrationQueryIds.listSessions) });
			// The executions the delete cascaded away are gone from that list too.
			await queryClient.invalidateQueries({ queryKey: integrationInvalidationKey(integrationQueryIds.listExecutions) });
		},
	});
}

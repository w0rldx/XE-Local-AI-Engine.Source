import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	deleteIntegrationSessionMutation,
	listIntegrationSessionsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toIntegrationSession } from "@/features/integrations/models/IntegrationMappers";
import { type IntegrationSessionFilters, integrationListLimit } from "@/features/integrations/models/IntegrationModels";
import { integrationInvalidationKey, integrationQueryIds } from "@/features/integrations/queries/useIntegrationTriggers";

// Server state for integration sessions. Both filters are server-side for the same reason the executions filters are:
// the list is a bounded window, so narrowing it in the browser would hide sessions that match the filter but fall
// outside the window.

export function useIntegrationSessions(filters: IntegrationSessionFilters = {}) {
	return useQuery({
		...withResponseValidation(
			listIntegrationSessionsOptions({
				query: {
					...(filters.triggerId === undefined ? {} : { triggerId: filters.triggerId }),
					...(filters.status === undefined ? {} : { status: filters.status }),
					limit: integrationListLimit,
					offset: 0,
				},
			}),
		),
		select: (data) => data.items.map(toIntegrationSession),
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

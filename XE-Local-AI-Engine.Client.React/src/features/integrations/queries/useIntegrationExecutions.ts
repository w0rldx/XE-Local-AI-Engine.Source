import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	cancelIntegrationExecutionMutation,
	getIntegrationExecutionEventsOptions,
	getIntegrationExecutionOptions,
	listIntegrationExecutionsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import {
	toIntegrationExecution,
	toIntegrationExecutionDetail,
	toIntegrationExecutionEvent,
} from "@/features/integrations/models/IntegrationMappers";
import {
	type IntegrationExecutionFilters,
	integrationEventLimit,
	integrationListLimit,
} from "@/features/integrations/models/IntegrationModels";
import { integrationInvalidationKey, integrationQueryIds } from "@/features/integrations/queries/useIntegrationTriggers";

// Server state for integration executions. Every filter is a QUERY PARAMETER the backend accepts, so it rides in the
// generated query key too: changing a filter refetches against the whole table rather than narrowing the window the
// last read happened to return.

interface IntegrationQueryOptions {
	readonly enabled?: boolean;
	readonly refetchInterval?: number | false;
}

export function useIntegrationExecutions(filters: IntegrationExecutionFilters = {}, options: IntegrationQueryOptions = {}) {
	return useQuery({
		...withResponseValidation(
			listIntegrationExecutionsOptions({
				query: {
					...(filters.triggerId === undefined ? {} : { triggerId: filters.triggerId }),
					...(filters.sessionId === undefined ? {} : { sessionId: filters.sessionId }),
					...(filters.status === undefined ? {} : { status: filters.status }),
					// One bounded window at the validator's maximum, always from the start of the server's ordering.
					// The response carries no total count, so there is nothing a page navigator could honestly show.
					limit: integrationListLimit,
					offset: 0,
				},
			}),
		),
		enabled: options.enabled ?? true,
		refetchInterval: options.refetchInterval,
		select: (data) => data.items.map(toIntegrationExecution),
	});
}

/** The per-execution audit record (principal, request id, key prefix) the list projection does not carry. */
export function useIntegrationExecution(executionId: string | null) {
	return useQuery({
		...withResponseValidation(getIntegrationExecutionOptions({ path: { executionId: executionId ?? "" } })),
		enabled: executionId !== null,
		select: toIntegrationExecutionDetail,
	});
}

/**
 * The persisted timeline, always read WHOLE (`sinceSeq: 0`) rather than merged from a watermark. Only nine event
 * types are ever persisted and neither assistant type is among them, so the list is bounded by construction and a
 * full re-read each tick is cheaper than a merge. `sinceSeq` exists on the endpoint for the SSE-resume caller.
 */
export function useIntegrationExecutionEvents(executionId: string | null, options: IntegrationQueryOptions = {}) {
	return useQuery({
		...withResponseValidation(
			getIntegrationExecutionEventsOptions({
				path: { executionId: executionId ?? "" },
				query: { sinceSeq: 0, limit: integrationEventLimit },
			}),
		),
		enabled: executionId !== null && (options.enabled ?? true),
		refetchInterval: options.refetchInterval,
		select: (data) => data.items.map(toIntegrationExecutionEvent),
	});
}

/**
 * Cancellation is REQUESTED, not applied: the endpoint answers 202 and a running turn stops when it observes the
 * token, so the row leaves its active status on a later poll rather than on this response. A 409 means the execution
 * already reached a terminal state, which the invalidation below resolves by showing what it actually became.
 */
export function useCancelIntegrationExecution() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(cancelIntegrationExecutionMutation()),
		onSuccess: () =>
			queryClient.invalidateQueries({ queryKey: integrationInvalidationKey(integrationQueryIds.listExecutions) }),
	});
}

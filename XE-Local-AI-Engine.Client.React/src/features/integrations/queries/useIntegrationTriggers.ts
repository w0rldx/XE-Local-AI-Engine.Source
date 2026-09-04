import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	createIntegrationTriggerMutation,
	deleteIntegrationTriggerMutation,
	listIntegrationTriggersOptions,
	updateIntegrationTriggerMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toIntegrationTrigger } from "@/features/integrations/models/IntegrationMappers";

// Server state for the integration triggers surface. Reads use the generated hey-api `*Options()` (which wire the
// shared axios instance + TanStack Query AbortSignal automatically) wrapped in withResponseValidation, plus a
// `select` that maps the optional-field wire response into the stricter domain view-model. Mutations invalidate by
// the generated query key's `_id` discriminator (partial-object match), so every cached variant refetches.

/** Generated operation ids used as invalidation discriminators. */
export const integrationQueryIds = {
	listTriggers: "listIntegrationTriggers",
	listKeys: "listIntegrationApiKeys",
	listExecutions: "listIntegrationExecutions",
	listSessions: "listIntegrationSessions",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one integrations endpoint. */
export function integrationInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

export function useIntegrationTriggers() {
	return useQuery({
		...withResponseValidation(listIntegrationTriggersOptions()),
		select: (data) => data.items.map(toIntegrationTrigger),
	});
}

function invalidateTriggers(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: integrationInvalidationKey(integrationQueryIds.listTriggers) });
}

export function useCreateIntegrationTrigger() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(createIntegrationTriggerMutation()),
		onSuccess: () => invalidateTriggers(queryClient),
	});
}

export function useUpdateIntegrationTrigger() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(updateIntegrationTriggerMutation()),
		onSuccess: () => invalidateTriggers(queryClient),
	});
}

export function useDeleteIntegrationTrigger() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(deleteIntegrationTriggerMutation()),
		onSuccess: () => invalidateTriggers(queryClient),
	});
}

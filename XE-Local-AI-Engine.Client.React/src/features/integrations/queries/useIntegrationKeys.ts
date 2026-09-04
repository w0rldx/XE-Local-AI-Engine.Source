import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	generateIntegrationApiKeyMutation,
	listIntegrationApiKeysOptions,
	revokeIntegrationApiKeyMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toIntegrationApiKey } from "@/features/integrations/models/IntegrationMappers";
import { integrationInvalidationKey, integrationQueryIds } from "@/features/integrations/queries/useIntegrationTriggers";

// Server state for the integration API keys surface. The generate response carries the show-once plaintext, which the
// panel captures in the mutation's onSuccess callback and holds in component state, because the node persists only a
// SHA-256 digest and can never supply it again. Nothing here may outlive that: the plaintext is the mutation's `data`,
// so it sits in TanStack's MutationCache until the entry is collected. `gcTime: 0` plus the page's `reset()` right
// after the capture drops it on the next tick instead of leaving it there for the default five-minute gc window.

export function useIntegrationKeys() {
	return useQuery({
		...withResponseValidation(listIntegrationApiKeysOptions()),
		select: (data) => data.items.map(toIntegrationApiKey),
	});
}

function invalidateKeys(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: integrationInvalidationKey(integrationQueryIds.listKeys) });
}

export function useGenerateIntegrationApiKey() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(generateIntegrationApiKeyMutation()),
		// Collect the entry the moment the page's `reset()` detaches this observer. Resetting alone only removes the
		// observer; without a zero gc time the plaintext-bearing `data` stays in the cache for the default window.
		gcTime: 0,
		onSuccess: () => invalidateKeys(queryClient),
	});
}

export function useRevokeIntegrationApiKey() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(revokeIntegrationApiKeyMutation()),
		onSuccess: () => invalidateKeys(queryClient),
	});
}

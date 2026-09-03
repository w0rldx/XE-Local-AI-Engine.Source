import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	generateIntegrationApiKeyMutation,
	listIntegrationApiKeysOptions,
	revokeIntegrationApiKeyMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toIntegrationApiKey } from "@/features/integrations/models/IntegrationMappers";
import { integrationInvalidationKey, integrationQueryIds } from "@/features/integrations/queries/useIntegrationTriggers";

// Server state for the integration API keys surface. The generate response carries the show-once plaintext, which is
// NEVER cached or stored: the panel captures it in the mutation's onSuccess callback and holds it in component
// state, because the node persists only a SHA-256 digest and can never supply it again.

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

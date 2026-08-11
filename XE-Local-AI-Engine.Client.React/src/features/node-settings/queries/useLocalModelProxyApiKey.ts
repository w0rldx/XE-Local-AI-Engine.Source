import {
	generateLocalModelProxyApiKeyMutation,
	getLocalModelProxyApiKeyOptions,
	getLocalModelProxyApiKeyQueryKey,
	revokeLocalModelProxyApiKeyMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

// Server state for the INBOUND local-model-proxy credential — the key an external OpenAI-compatible tool (LiteLLM,
// Continue, a local agent) presents to this node's OpenAI-compatible proxy endpoint so it can use the local model as
// a plain OpenAI provider. Mirrors useMcpServerApiKey's shape exactly; the two credentials are unrelated and share
// no cache.
//
// Generate and revoke both invalidate this query rather than writing the response into the cache, so the panel always
// renders server truth. That matters here more than usual: generating REPLACES the previous key, and a stale cached
// value would show an operator a credential that no longer authenticates.
//
// This query carries NO key, and cannot: the node stores only a SHA-256 digest, so the GET has nothing to return. The
// plaintext exists solely in the generate mutation's response and must be held in component state by whoever needs to
// display it — never written into this cache, which is refetched and would silently drop it.

export interface LocalModelProxyApiKeyView {
	configured: boolean;
	prefix: string | null;
	createdAt: string | null;
	lastUsedAt: string | null;
	endpointUrl: string;
}

export function useLocalModelProxyApiKey() {
	return useQuery({
		...withResponseValidation(getLocalModelProxyApiKeyOptions()),
		select: (data): LocalModelProxyApiKeyView => ({
			configured: data.configured ?? false,
			prefix: data.apiKey?.prefix ?? null,
			createdAt: data.apiKey?.createdAt ?? null,
			lastUsedAt: data.apiKey?.lastUsedAt ?? null,
			endpointUrl: data.endpointUrl ?? "",
		}),
	});
}

function invalidateApiKey(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: getLocalModelProxyApiKeyQueryKey() });
}

export function useGenerateLocalModelProxyApiKey() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(generateLocalModelProxyApiKeyMutation()),
		onSuccess: () => invalidateApiKey(queryClient),
	});
}

export function useRevokeLocalModelProxyApiKey() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(revokeLocalModelProxyApiKeyMutation()),
		onSuccess: () => invalidateApiKey(queryClient),
	});
}

import {
	generateMcpServerApiKeyMutation,
	getMcpServerApiKeyOptions,
	getMcpServerApiKeyQueryKey,
	revokeMcpServerApiKeyMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

// Server state for the INBOUND MCP credential — the key an external MCP client (Claude Code, an IDE) presents to this
// node's own MCP endpoint. This is the opposite direction to useMcpServers, which manages the OUTBOUND registrations
// this node connects to; the two share no cache.
//
// Generate and revoke both invalidate this query rather than writing the response into the cache, so the panel always
// renders server truth. That matters here more than usual: generating REPLACES the previous key, and a stale cached
// value would show an operator a credential that no longer authenticates.
//
// This query carries NO key, and cannot: the node stores only a SHA-256 digest, so the GET has nothing to return. The
// plaintext exists solely in the generate mutation's response and must be held in component state by whoever needs to
// display it — never written into this cache, which is refetched and would silently drop it.

export interface McpServerApiKeyView {
	configured: boolean;
	prefix: string | null;
	scope: "delegate" | "agentic" | null;
	createdAt: string | null;
	lastUsedAt: string | null;
	endpointUrl: string;
}

export function useMcpServerApiKey() {
	return useQuery({
		...withResponseValidation(getMcpServerApiKeyOptions()),
		select: (data): McpServerApiKeyView => ({
			configured: data.configured ?? false,
			prefix: data.apiKey?.prefix ?? null,
			scope: data.apiKey?.scope ?? null,
			createdAt: data.apiKey?.createdAt ?? null,
			lastUsedAt: data.apiKey?.lastUsedAt ?? null,
			endpointUrl: data.endpointUrl ?? "",
		}),
	});
}

function invalidateApiKey(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: getMcpServerApiKeyQueryKey() });
}

export function useGenerateMcpServerApiKey() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(generateMcpServerApiKeyMutation()),
		onSuccess: () => invalidateApiKey(queryClient),
	});
}

export function useRevokeMcpServerApiKey() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(revokeMcpServerApiKeyMutation()),
		onSuccess: () => invalidateApiKey(queryClient),
	});
}

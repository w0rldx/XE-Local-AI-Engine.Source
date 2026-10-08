import { useQuery } from "@tanstack/react-query";

import { type ContextEstimateRequest, nodeChatAdapter } from "@/features/chat/api/NodeChatAdapter";
import { nodeChatQueryKeys } from "@/features/chat/queries/NodeChatQueryKeys";

/**
 * Pre-send estimate of the fixed context parts (system prompt, instructions, tools) for the selected model and agent.
 * Fetched only while `enabled` (the popover is open and no last-round snapshot exists yet). A blank model name is the
 * node's default model, resolved server-side like a send without a model. Resolves to null when the node does not know
 * the model (404). No interval: the estimate changes only when an input of the request changes, and
 * every input is part of the key.
 */
export function useContextEstimate(request: ContextEstimateRequest, { enabled }: { enabled: boolean }) {
	return useQuery({
		queryKey: nodeChatQueryKeys.contextEstimate(request),
		queryFn: ({ signal }) => nodeChatAdapter.getContextEstimate(request, { signal }),
		enabled,
		staleTime: 30_000,
	});
}

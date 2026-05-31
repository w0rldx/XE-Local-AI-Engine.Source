import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	createGoldenConversation,
	deleteGoldenConversation,
	listGoldenConversations,
} from "@/features/agents/api/GoldenConversationsApi";
import type { CreateGoldenConversationRequestDto } from "@/features/agents/models/GoldenConversationModels";
import { goldenConversationsQueryKeys } from "@/features/agents/queries/GoldenConversationsQueryKeys";

// Server state for an agent's golden conversation set (Playbook P4). The read wires the TanStack Query AbortSignal
// into the axios request (per repo React standards); the create/delete mutations invalidate the per-agent cache on
// success. The query is disabled when no persisted agent is selected so the panel never fetches with an empty id.

export function useGoldenConversations(agentDefinitionId: string | null) {
	return useQuery({
		queryKey: goldenConversationsQueryKeys.byAgent(agentDefinitionId ?? ""),
		queryFn: ({ signal }) => listGoldenConversations(agentDefinitionId ?? "", { signal }),
		enabled: agentDefinitionId !== null && agentDefinitionId.length > 0,
	});
}

export function useCreateGoldenConversation(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (request: CreateGoldenConversationRequestDto) =>
			createGoldenConversation(agentDefinitionId, request),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: goldenConversationsQueryKeys.byAgent(agentDefinitionId) });
		},
	});
}

export function useDeleteGoldenConversation(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (goldenId: string) => deleteGoldenConversation(agentDefinitionId, goldenId),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: goldenConversationsQueryKeys.byAgent(agentDefinitionId) });
		},
	});
}

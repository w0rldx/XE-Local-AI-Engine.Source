import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	createPlaybookAction,
	deletePlaybookAction,
	listPlaybookActions,
	updatePlaybookAction,
} from "@/features/agents/api/PlaybookActionsApi";
import type { SavePlaybookActionRequestDto } from "@/features/agents/models/PlaybookActionModels";
import { playbookQueryKeys } from "@/features/agents/queries/PlaybookQueryKeys";

// Server state for an agent's playbook. Reads wire the TanStack Query AbortSignal into the axios request (per
// repo React standards); mutations invalidate the per-agent action cache on success. Queries are disabled when
// no agent is selected so the panel does not fetch with an empty id.

export function usePlaybookActions(agentDefinitionId: string | null) {
	return useQuery({
		queryKey: playbookQueryKeys.byAgent(agentDefinitionId ?? ""),
		queryFn: ({ signal }) => listPlaybookActions(agentDefinitionId ?? "", { signal }),
		enabled: agentDefinitionId !== null && agentDefinitionId.length > 0,
	});
}

export function useCreatePlaybookAction(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (request: SavePlaybookActionRequestDto) => createPlaybookAction(agentDefinitionId, request),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: playbookQueryKeys.byAgent(agentDefinitionId) });
		},
	});
}

export interface UpdatePlaybookActionVariables {
	actionId: string;
	request: SavePlaybookActionRequestDto;
}

export function useUpdatePlaybookAction(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: ({ actionId, request }: UpdatePlaybookActionVariables) =>
			updatePlaybookAction(agentDefinitionId, actionId, request),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: playbookQueryKeys.byAgent(agentDefinitionId) });
		},
	});
}

export function useDeletePlaybookAction(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (actionId: string) => deletePlaybookAction(agentDefinitionId, actionId),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: playbookQueryKeys.byAgent(agentDefinitionId) });
		},
	});
}

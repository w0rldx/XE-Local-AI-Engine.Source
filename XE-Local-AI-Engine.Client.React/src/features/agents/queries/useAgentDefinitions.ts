import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	createAgentDefinition,
	deleteAgentDefinition,
	listAgentDefinitions,
	listToolCapableModels,
	type SaveAgentDefinitionRequestDto,
	updateAgentDefinition,
} from "@/features/agents/api/AgentDefinitionsApi";
import { agentDefinitionsQueryKeys } from "@/features/agents/queries/AgentDefinitionsQueryKeys";

// Server state for the agent-management surface. All reads wire the TanStack Query AbortSignal into the
// axios request (per repo React standards); mutations invalidate the definition cache on success.

export function useAgentDefinitions() {
	return useQuery({
		queryKey: agentDefinitionsQueryKeys.list(),
		queryFn: ({ signal }) => listAgentDefinitions({ signal }),
	});
}

// Tool-capable model names. Empty list means "capability source not available" and is treated by the page as
// "do not enforce" so the surface keeps working before Lane 3 exposes the endpoint.
export function useToolCapableModels() {
	return useQuery({
		queryKey: agentDefinitionsQueryKeys.toolCapableModels(),
		queryFn: ({ signal }) => listToolCapableModels({ signal }),
	});
}

export function useCreateAgentDefinition() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (request: SaveAgentDefinitionRequestDto) => createAgentDefinition(request),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: agentDefinitionsQueryKeys.all() });
		},
	});
}

export interface UpdateAgentDefinitionVariables {
	id: string;
	request: SaveAgentDefinitionRequestDto;
}

export function useUpdateAgentDefinition() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: ({ id, request }: UpdateAgentDefinitionVariables) => updateAgentDefinition(id, request),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: agentDefinitionsQueryKeys.all() });
		},
	});
}

export function useDeleteAgentDefinition() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (id: string) => deleteAgentDefinition(id),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: agentDefinitionsQueryKeys.all() });
		},
	});
}

import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	analyzePlaybook,
	createPlaybookAction,
	deletePlaybookAction,
	listPlaybookActions,
	promoteSuggested,
	rejectSuggested,
	runEval,
	updatePlaybookAction,
	updateSuggested,
} from "@/features/agents/api/PlaybookActionsApi";
import type {
	PlaybookAction,
	SavePlaybookActionRequestDto,
	SaveSuggestedActionRequestDto,
} from "@/features/agents/models/PlaybookActionModels";
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

// Playbook P3 — analysis governance mutations. analyze runs the analysis agent (returning the freshly proposed
// Suggested actions so the panel can react to an empty result); promote/reject move a single Suggested action.
// All three invalidate the per-agent action cache on success so the Suggested section reflects the new state.

export function useAnalyzePlaybook(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation<PlaybookAction[], unknown, void>({
		mutationFn: () => analyzePlaybook(agentDefinitionId),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: playbookQueryKeys.byAgent(agentDefinitionId) });
		},
	});
}

export function usePromoteSuggestedAction(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (actionId: string) => promoteSuggested(agentDefinitionId, actionId),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: playbookQueryKeys.byAgent(agentDefinitionId) });
		},
	});
}

// Playbook P4 — run the eval gate for a single Suggested action against the agent's golden set. The mutation
// records the EvalResult; invalidating the per-agent cache refreshes the Suggested row's eval badge + the
// Approve gate (Approve stays disabled until evalResult.passed).
export function useRunEval(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (actionId: string) => runEval(agentDefinitionId, actionId),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: playbookQueryKeys.byAgent(agentDefinitionId) });
		},
	});
}

export function useRejectSuggestedAction(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (actionId: string) => rejectSuggested(agentDefinitionId, actionId),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: playbookQueryKeys.byAgent(agentDefinitionId) });
		},
	});
}

export interface UpdateSuggestedActionVariables {
	actionId: string;
	request: SaveSuggestedActionRequestDto;
}

// Edit a pending Suggested action via the dedicated `/suggested` route (the manual PUT 404s on Analysis
// provenance). The action stays Suggested; invalidating the cache refreshes the Suggested section with the edit.
export function useUpdateSuggestedAction(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: ({ actionId, request }: UpdateSuggestedActionVariables) =>
			updateSuggested(agentDefinitionId, actionId, request),
		onSuccess: async () => {
			await queryClient.invalidateQueries({ queryKey: playbookQueryKeys.byAgent(agentDefinitionId) });
		},
	});
}

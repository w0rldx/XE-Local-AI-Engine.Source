import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	analyzePlaybookMutation,
	createPlaybookActionMutation,
	deletePlaybookActionMutation,
	listAgentPlaybookActionsOptions,
	promoteSuggestedPlaybookActionMutation,
	rejectSuggestedPlaybookActionMutation,
	runPlaybookActionEvalMutation,
	updatePlaybookActionMutation,
	updateSuggestedPlaybookActionMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toPlaybookAction, toPlaybookActionRequestBody, toPromoteError } from "@/features/agents/models/PlaybookActionMappers";
import type {
	PlaybookAction,
	SavePlaybookActionRequestDto,
	SaveSuggestedActionRequestDto,
} from "@/features/agents/models/PlaybookActionModels";

// Server state for an agent's playbook. The read uses the generated hey-api `*Options()` (which wires the shared
// axios instance + TanStack Query AbortSignal automatically), wrapped in withResponseValidation so a zod
// response-shape failure surfaces as an ApiError; a TanStack `select` maps the optional-field generated response
// into the stricter domain view-model. Mutations dispatch the domain-friendly call signature into the generated
// `*Mutation()`'s mutationFn and invalidate the per-agent action list on success (onSettled where a 409 is
// expected). The query is disabled when no agent is selected so the panel never fetches with an empty id.

// The generated query key for the action list is `[{ _id: "listAgentPlaybookActions", ... }]`. Invalidating with
// just the `_id` partial object matches every cached agent variant of the endpoint (TanStack partial-object
// matching) — equivalent to the former per-agent invalidation, broadened to all agents the same way the scheduler
// reference does. Centralized here so the literal `_id` key — which trips biome's naming-convention rule — is
// constructed in exactly one place.
export const playbookQueryIds = {
	listActions: "listAgentPlaybookActions",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of the playbook-action list. */
export function playbookInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

function invalidateActions(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({
		queryKey: playbookInvalidationKey(playbookQueryIds.listActions),
	});
}

export function usePlaybookActions(agentDefinitionId: string | null) {
	return useQuery({
		...withResponseValidation(listAgentPlaybookActionsOptions({ path: { agentDefinitionId: agentDefinitionId ?? "" } })),
		enabled: agentDefinitionId !== null && agentDefinitionId.length > 0,
		select: (data) => (data.items ?? []).map(toPlaybookAction),
	});
}

export function useCreatePlaybookAction(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (request: SavePlaybookActionRequestDto): Promise<PlaybookAction> => {
			const options = withResponseValidation(createPlaybookActionMutation());
			const data = await options.mutationFn?.(
				{ path: { agentDefinitionId }, body: toPlaybookActionRequestBody(request) },
				undefined as never,
			);
			return toPlaybookAction(data ?? {});
		},
		onSuccess: () => invalidateActions(queryClient),
	});
}

export interface UpdatePlaybookActionVariables {
	actionId: string;
	request: SavePlaybookActionRequestDto;
}

export function useUpdatePlaybookAction(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async ({ actionId, request }: UpdatePlaybookActionVariables): Promise<PlaybookAction> => {
			const options = withResponseValidation(updatePlaybookActionMutation());
			const data = await options.mutationFn?.(
				{ path: { agentDefinitionId, actionId }, body: toPlaybookActionRequestBody(request) },
				undefined as never,
			);
			return toPlaybookAction(data ?? {});
		},
		onSuccess: () => invalidateActions(queryClient),
	});
}

export function useDeletePlaybookAction(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (actionId: string): Promise<void> => {
			const options = withResponseValidation(deletePlaybookActionMutation());
			await options.mutationFn?.({ path: { agentDefinitionId, actionId } }, undefined as never);
		},
		onSuccess: () => invalidateActions(queryClient),
	});
}

// Analysis governance mutations. analyze runs the analysis agent (returning the freshly proposed
// Suggested actions so the panel can react to an empty result); promote/reject move a single Suggested action.
// All invalidate the per-agent action list so the Suggested section reflects the new state.

export function useAnalyzePlaybook(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation<PlaybookAction[], unknown, void>({
		mutationFn: async (): Promise<PlaybookAction[]> => {
			const options = withResponseValidation(analyzePlaybookMutation());
			const data = await options.mutationFn?.({ path: { agentDefinitionId } }, undefined as never);
			return (data?.items ?? []).map(toPlaybookAction);
		},
		onSuccess: () => invalidateActions(queryClient),
	});
}

// Promote a Suggested analysis action to Enabled (operator approves the proposal). The promote is
// eval-gated AND enabled-set-cap-gated: when the eval has not passed (or the cap is reached) the backend returns
// 409 with a typed { status, reason } body. The shared interceptor wraps it into an ApiError; toPromoteError
// recovers the typed PromoteConflictError so the panel can show the precise reason. Uses onSettled (not onSuccess)
// because a blocked promote rejects with 409 — onSuccess would skip the refresh and leave a stale row; onSettled
// refreshes the list either way.
export function usePromoteSuggestedAction(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (actionId: string): Promise<PlaybookAction> => {
			try {
				const options = withResponseValidation(promoteSuggestedPlaybookActionMutation());
				const data = await options.mutationFn?.({ path: { agentDefinitionId, actionId } }, undefined as never);
				return toPlaybookAction(data ?? {});
			} catch (error) {
				throw toPromoteError(error);
			}
		},
		onSettled: () => invalidateActions(queryClient),
	});
}

// Run the eval gate for a single Suggested action against the agent's golden set. The mutation
// records the EvalResult; invalidating the per-agent list refreshes the Suggested row's eval badge + the Approve
// gate (Approve stays disabled until evalResult.passed).
export function useRunEval(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (actionId: string): Promise<PlaybookAction> => {
			const options = withResponseValidation(runPlaybookActionEvalMutation());
			const data = await options.mutationFn?.({ path: { agentDefinitionId, actionId } }, undefined as never);
			return toPlaybookAction(data ?? {});
		},
		onSuccess: () => invalidateActions(queryClient),
	});
}

export function useRejectSuggestedAction(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (actionId: string): Promise<PlaybookAction> => {
			const options = withResponseValidation(rejectSuggestedPlaybookActionMutation());
			const data = await options.mutationFn?.({ path: { agentDefinitionId, actionId } }, undefined as never);
			return toPlaybookAction(data ?? {});
		},
		onSuccess: () => invalidateActions(queryClient),
	});
}

export interface UpdateSuggestedActionVariables {
	actionId: string;
	request: SaveSuggestedActionRequestDto;
}

// Edit a pending Suggested action via the dedicated `/suggested` route (the manual PUT 404s on Analysis
// provenance). The action stays Suggested; invalidating the list refreshes the Suggested section with the edit.
export function useUpdateSuggestedAction(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async ({ actionId, request }: UpdateSuggestedActionVariables): Promise<PlaybookAction> => {
			const options = withResponseValidation(updateSuggestedPlaybookActionMutation());
			const data = await options.mutationFn?.({ path: { agentDefinitionId, actionId }, body: request }, undefined as never);
			return toPlaybookAction(data ?? {});
		},
		onSuccess: () => invalidateActions(queryClient),
	});
}

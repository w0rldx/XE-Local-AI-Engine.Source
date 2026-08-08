import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import type { XeLocalAiEngineClientEndpointsAgentsV1CreateAgentDefinitionRequest } from "@/core/api/generated";
import {
	createAgentDefinitionMutation,
	deleteAgentDefinitionMutation,
	getToolCapableModelsOptions,
	listAgentDefinitionsOptions,
	updateAgentDefinitionMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toAgentDefinition, toAgentDefinitions } from "@/features/agents/models/AgentDefinitionMappers";

// Server state for the agent-management surface. Reads use the generated hey-api `*Options()` (which wire the shared
// axios instance + TanStack Query AbortSignal automatically) and a TanStack `select` that maps the optional-field
// generated response into the stricter domain view-model. Every generated options object is wrapped in
// withResponseValidation so a zod response-shape failure surfaces as an ApiError (never a raw ZodError). The
// create/update/delete mutations keep their domain-friendly call signatures (the page/form pass domain values) and
// dispatch into the generated `{ path, body }` envelope, then invalidate the definitions list on success.

// The generated query keys are single-element arrays `[{ _id: "<operationId>", ... }]`. Invalidating with just the
// `_id` partial object matches every cached variant of that endpoint (TanStack partial-object matching). The
// operationIds equal the generated SDK fn names. Centralized here so the literal `_id` key — which trips biome's
// naming-convention rule — is constructed in exactly one place.
export const agentDefinitionsQueryIds = {
	list: "listAgentDefinitions",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one agent-definition endpoint. */
export function agentDefinitionsInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

export function useAgentDefinitions() {
	return useQuery({
		...withResponseValidation(listAgentDefinitionsOptions()),
		select: toAgentDefinitions,
	});
}

// Tool-capable model names. Empty list means "capability source not available" and is treated by the page as
// "do not enforce" so the surface keeps working before the node populates the capability. The generated response
// carries `{ models?: string[] }`; the select maps it to the bare `string[]` the page consumes.
export function useToolCapableModels() {
	return useQuery({
		...withResponseValidation(getToolCapableModelsOptions()),
		select: (data) => data.models ?? [],
	});
}

function invalidateList(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({
		queryKey: agentDefinitionsInvalidationKey(agentDefinitionsQueryIds.list),
	});
}

// Create keeps the domain save-request variable (built by toSaveAgentDefinitionRequest at the page) and dispatches
// it into the generated mutationFn's `{ body }` envelope. Returns the mapped created definition (the POST response
// body is the bare AgentDefinitionResponse) so the page can react to it.
export function useCreateAgentDefinition() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (request: XeLocalAiEngineClientEndpointsAgentsV1CreateAgentDefinitionRequest) => {
			const options = withResponseValidation(createAgentDefinitionMutation());
			const data = await options.mutationFn?.({ body: request }, undefined as never);
			return data ? toAgentDefinition(data) : undefined;
		},
		onSuccess: () => invalidateList(queryClient),
	});
}

export interface UpdateAgentDefinitionVariables {
	id: string;
	request: XeLocalAiEngineClientEndpointsAgentsV1CreateAgentDefinitionRequest;
}

// Update keeps the domain `{ id, request }` variable and dispatches it into the generated `{ path, body }` envelope
// (the wire path param is `agentDefinitionId`). The generated client serializes the path itself, so no hand
// encoding is needed. Returns the mapped updated definition (bare AgentDefinitionResponse body).
export function useUpdateAgentDefinition() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async ({ id, request }: UpdateAgentDefinitionVariables) => {
			const options = withResponseValidation(updateAgentDefinitionMutation());
			const data = await options.mutationFn?.({ path: { agentDefinitionId: id }, body: request }, undefined as never);
			return data ? toAgentDefinition(data) : undefined;
		},
		onSuccess: () => invalidateList(queryClient),
	});
}

// Delete keeps the domain id variable and dispatches it into the generated `{ path }` envelope (wire path param
// `agentDefinitionId`).
export function useDeleteAgentDefinition() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (id: string) => {
			const options = withResponseValidation(deleteAgentDefinitionMutation());
			await options.mutationFn?.({ path: { agentDefinitionId: id } }, undefined as never);
		},
		onSuccess: () => invalidateList(queryClient),
	});
}

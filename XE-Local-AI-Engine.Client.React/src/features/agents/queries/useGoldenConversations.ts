import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	approveGoldenConversationMutation,
	createGoldenConversationMutation,
	deleteGoldenConversationMutation,
	harvestGoldenConversationsMutation,
	listGoldenConversationsOptions,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import {
	toCreateGoldenConversationRequest,
	toGoldenConversations,
	toGoldenHarvestResult,
} from "@/features/agents/models/GoldenConversationMappers";
import type { CreateGoldenConversationRequestDto, GoldenHarvestResult } from "@/features/agents/models/GoldenConversationModels";

// Server state for an agent's golden conversation set (with harvested candidates). The read uses the generated
// hey-api `*Options()` (which wires the shared axios instance + TanStack Query AbortSignal automatically) and a
// TanStack `select` that maps the optional-field generated response into the stricter domain view-model. Every
// generated options object is wrapped in withResponseValidation so a zod response-shape failure surfaces as an
// ApiError (never a raw ZodError). The create/delete/harvest/approve mutations invalidate the per-endpoint list
// cache on success. The list query is disabled when no persisted agent is selected so the panel never fetches with
// an empty id. The harvest/approve POSTs are route-only — the generated client sends the empty body the backend
// expects (the request bodies are `{ [key: string]: never }`).

// The generated query keys are single-element arrays `[{ _id: "<operationId>", ... }]`. Invalidating with just the
// `_id` partial object matches every cached variant of that endpoint (TanStack partial-object matching), so every
// per-agent variant of the list refetches. The operationIds equal the generated SDK fn names. Centralized here so
// the literal `_id` key — which trips biome's naming-convention rule — is constructed in exactly one place.
export const goldenConversationsQueryIds = {
	list: "listGoldenConversations",
} as const;

/** Builds the partial generated-query-key filter that matches every cached variant of one golden-conversation endpoint. */
export function goldenConversationsInvalidationKey(operationId: string): readonly [{ _id: string }] {
	// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
	return [{ _id: operationId }];
}

export function useGoldenConversations(agentDefinitionId: string | null) {
	return useQuery({
		...withResponseValidation(listGoldenConversationsOptions({ path: { agentDefinitionId: agentDefinitionId ?? "" } })),
		enabled: agentDefinitionId !== null && agentDefinitionId.length > 0,
		select: toGoldenConversations,
	});
}

function invalidateList(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({
		queryKey: goldenConversationsInvalidationKey(goldenConversationsQueryIds.list),
	});
}

// Create keeps the domain `CreateGoldenConversationRequestDto` variable and dispatches it into the generated
// mutationFn's `{ path, body }` envelope so the panel never touches the wire shape. The bound agentDefinitionId
// rides the path. Returns the mapped created case (the POST response is the bare case, not an envelope).
export function useCreateGoldenConversation(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (request: CreateGoldenConversationRequestDto) => {
			const options = withResponseValidation(createGoldenConversationMutation());
			return options.mutationFn?.(
				{ path: { agentDefinitionId }, body: toCreateGoldenConversationRequest(request) },
				undefined as never,
			);
		},
		onSuccess: () => invalidateList(queryClient),
	});
}

// Delete keeps the domain golden id variable and dispatches it into the generated `{ path }` envelope (the wire path
// param is `goldenConversationId`). The generated client serializes the path itself, so no hand encoding is needed.
export function useDeleteGoldenConversation(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (goldenId: string) => {
			const options = withResponseValidation(deleteGoldenConversationMutation());
			await options.mutationFn?.({ path: { agentDefinitionId, goldenConversationId: goldenId } }, undefined as never);
		},
		onSuccess: () => invalidateList(queryClient),
	});
}

// Harvest golden candidates from the agent's thumbs-up turns. The page-facing variable is `void` (the bound
// agentDefinitionId is the only input), so the hook adapts it to the generated `{ path }` envelope and maps the
// scan/created/duplicate/skipped counts for the success toast. Invalidates the per-agent golden list so newly-staged
// (inert) candidates appear in the pending-review sub-section.
export function useHarvestGolden(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (): Promise<GoldenHarvestResult> => {
			const options = withResponseValidation(harvestGoldenConversationsMutation());
			const data = await options.mutationFn?.({ path: { agentDefinitionId } }, undefined as never);
			return toGoldenHarvestResult(data ?? {});
		},
		onSuccess: () => invalidateList(queryClient),
	});
}

// Approve a harvested-but-disabled candidate into the active golden set. Keeps the domain golden id variable,
// dispatches it into the generated `{ path }` envelope (wire path param `goldenConversationId`), and invalidates the
// per-agent golden list so the approved case moves out of the pending-review sub-section into the active list.
export function useApproveGolden(agentDefinitionId: string) {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: async (goldenId: string) => {
			const options = withResponseValidation(approveGoldenConversationMutation());
			return options.mutationFn?.({ path: { agentDefinitionId, goldenConversationId: goldenId } }, undefined as never);
		},
		onSuccess: () => invalidateList(queryClient),
	});
}

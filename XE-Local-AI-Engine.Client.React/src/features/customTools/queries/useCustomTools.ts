import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import type { CreateCustomToolResponse, DeleteCustomToolResponse, UpdateCustomToolResponse } from "@/core/api/generated";
import {
	createCustomToolMutation,
	deleteCustomToolMutation,
	getCustomToolOptions,
	listCustomToolsOptions,
	listCustomToolsQueryKey,
	updateCustomToolMutation,
	validateExecutableMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toCustomToolView } from "@/features/customTools/models/CustomToolMappers";

// Server state for the node custom-tool library. Reads use the generated hey-api `*Options()` (which wire the shared
// axios instance + TanStack Query AbortSignal) with a `select` that maps the optional-field generated response into
// the stricter domain view-model. Every generated options/mutation object is wrapped in withResponseValidation so a
// zod response-shape failure surfaces as an ApiError. Mutations invalidate the list; an edit also invalidates the
// single-tool cache so a re-opened editor shows fresh values.

export function useCustomTools() {
	return useQuery({
		...withResponseValidation(listCustomToolsOptions()),
		select: (data) => (data.items ?? []).map(toCustomToolView),
	});
}

// Full single tool. Disabled until an id is supplied so the editor only fetches when a tool is actually being edited.
export function useCustomTool(id: string | null) {
	return useQuery({
		...withResponseValidation(getCustomToolOptions({ path: { customToolId: id ?? "" } })),
		enabled: id !== null,
		select: toCustomToolView,
	});
}

function invalidateCustomToolsList(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: listCustomToolsQueryKey() });
}

// Invalidates every single-tool (getCustomTool) query regardless of its id. The generated query key is a single-element
// array whose first element carries an `_id: "getCustomTool"` discriminator; matching only that field (TanStack does a
// partial deep match) refreshes all open single-tool caches so a re-opened editor shows the freshly edited values.
function invalidateAllSingleCustomTools(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({
		// biome-ignore lint/style/useNamingConvention: `_id` is the generated hey-api query-key discriminator field.
		queryKey: [{ _id: "getCustomTool" }],
	});
}

export function useCreateCustomTool() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(createCustomToolMutation()),
		onSuccess: (_data: CreateCustomToolResponse) => invalidateCustomToolsList(queryClient),
	});
}

export function useUpdateCustomTool() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(updateCustomToolMutation()),
		onSuccess: (_data: UpdateCustomToolResponse) =>
			Promise.all([invalidateCustomToolsList(queryClient), invalidateAllSingleCustomTools(queryClient)]).then(() => undefined),
	});
}

export function useDeleteCustomTool() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(deleteCustomToolMutation()),
		onSuccess: (_data: DeleteCustomToolResponse) => invalidateCustomToolsList(queryClient),
	});
}

// Probes a host executable path (desktop-only endpoint). Used by the Command editor's ProgramLaunch selector to
// confirm a path resolves to a regular, non-shell executable before the operator commits to it. Not a cached read —
// it is an on-demand action against the current filesystem, so it stays a mutation.
export function useValidateExecutable() {
	return useMutation(withResponseValidation(validateExecutableMutation()));
}

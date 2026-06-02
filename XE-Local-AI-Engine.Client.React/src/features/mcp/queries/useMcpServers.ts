import type {
	CreateMcpServerResponse,
	SetMcpServerEnabledResponse,
	UpdateMcpServerResponse,
} from "@/core/api/generated";
import {
	createMcpServerMutation,
	deleteMcpServerMutation,
	getMcpServerToolsOptions,
	listMcpServersOptions,
	listMcpServersQueryKey,
	setMcpServerEnabledMutation,
	updateMcpServerMutation,
} from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toMcpServerRegistration, toMcpServerToolsView } from "@/features/mcp/models/McpServerMappers";
import { toolCatalogQueryKeys } from "@/features/tools/queries/ToolCatalogQueryKeys";
import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

// Server state for the MCP-server management surface. Reads use the generated hey-api `*Options()` (which wire the
// shared axios instance + TanStack Query AbortSignal automatically) and a TanStack `select` that maps the
// optional-field generated response into the stricter domain view-model. Every generated options object is wrapped
// in withResponseValidation so a zod response-shape failure surfaces as an ApiError (never a raw ZodError).
// Mutations invalidate the registration cache; mutations that can change the ENABLED set (update/delete/enable/
// disable) also invalidate the dynamic tool catalog so the tool pickers re-fetch the new built-in + MCP tool set.
// Create is registration-only — a new server always persists DISABLED (the backend skips a connection refresh on
// create), so it never changes the catalog.

export function useMcpServers() {
	return useQuery({
		...withResponseValidation(listMcpServersOptions()),
		select: (data) => (data.items ?? []).map(toMcpServerRegistration),
	});
}

// Live discovered tools + connection status for one registered server. Disabled by default — the page enables
// the query only when the per-server tools panel is expanded so it does not poke every server on list load.
export function useMcpServerTools(id: string | null) {
	return useQuery({
		...withResponseValidation(getMcpServerToolsOptions({ path: { mcpServerId: id ?? "" } })),
		enabled: id !== null,
		select: toMcpServerToolsView,
	});
}

function invalidateServersList(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: listMcpServersQueryKey() });
}

async function invalidateServersAndCatalog(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	await Promise.all([
		invalidateServersList(queryClient),
		queryClient.invalidateQueries({ queryKey: toolCatalogQueryKeys.all() }),
	]);
}

export function useCreateMcpServer() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(createMcpServerMutation()),
		// A new server always persists DISABLED, so the tool catalog cannot have changed — only the registration
		// list needs refreshing. Invalidating the catalog here would force every tool picker to refetch for no
		// reason, so create is intentionally list-only.
		onSuccess: (_data: CreateMcpServerResponse) => invalidateServersList(queryClient),
	});
}

export function useUpdateMcpServer() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(updateMcpServerMutation()),
		onSuccess: (_data: UpdateMcpServerResponse) => invalidateServersAndCatalog(queryClient),
	});
}

export function useDeleteMcpServer() {
	const queryClient = useQueryClient();

	return useMutation({
		...withResponseValidation(deleteMcpServerMutation()),
		onSuccess: () => invalidateServersAndCatalog(queryClient),
	});
}

export interface SetMcpServerEnabledVariables {
	id: string;
	enabled: boolean;
}

// Enabling/disabling is one PATCH carrying an `{ enabled }` body. The hook keeps the domain `{ id, enabled }`
// variable and projects it to the generated `{ path: { mcpServerId }, body: { enabled } }` shape. The toggle
// changes the live tool catalog (connect/disconnect → tools appear/disappear), so the catalog cache is
// invalidated alongside the registration list.
export function useSetMcpServerEnabled() {
	const queryClient = useQueryClient();

	const options = withResponseValidation(setMcpServerEnabledMutation());

	return useMutation({
		mutationFn: ({ id, enabled }: SetMcpServerEnabledVariables): Promise<SetMcpServerEnabledResponse> =>
			options.mutationFn?.(
				{ path: { mcpServerId: id }, body: { enabled } },
				undefined as never,
			) as Promise<SetMcpServerEnabledResponse>,
		onSuccess: () => invalidateServersAndCatalog(queryClient),
	});
}

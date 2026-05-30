import { useMutation, useQuery, useQueryClient } from "@tanstack/react-query";

import {
	createMcpServer,
	deleteMcpServer,
	getMcpServerTools,
	listMcpServers,
	type SaveMcpServerRequestDto,
	setMcpServerEnabled,
	updateMcpServer,
} from "@/features/mcp/api/McpServersApi";
import { mcpServersQueryKeys } from "@/features/mcp/queries/McpServersQueryKeys";
import { toolCatalogQueryKeys } from "@/features/tools/queries/ToolCatalogQueryKeys";

// Server state for the MCP-server management surface. All reads wire the TanStack Query AbortSignal into the
// axios request (per repo React standards). Mutations invalidate the registration cache; mutations that can
// change the ENABLED set (update/delete/enable/disable) also invalidate the dynamic tool catalog so the tool
// pickers re-fetch the new built-in + MCP tool set. Create is registration-only — a new server always
// persists DISABLED (the backend skips a connection refresh on create), so it never changes the catalog.

export function useMcpServers() {
	return useQuery({
		queryKey: mcpServersQueryKeys.list(),
		queryFn: ({ signal }) => listMcpServers({ signal }),
	});
}

// Live discovered tools + connection status for one registered server. Disabled by default — the page enables
// the query only when the per-server tools panel is expanded so it does not poke every server on list load.
export function useMcpServerTools(id: string | null) {
	return useQuery({
		queryKey: mcpServersQueryKeys.tools(id ?? ""),
		queryFn: ({ signal }) => getMcpServerTools(id ?? "", { signal }),
		enabled: id !== null,
	});
}

function invalidateServersList(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: mcpServersQueryKeys.all() });
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
		mutationFn: (request: SaveMcpServerRequestDto) => createMcpServer(request),
		// A new server always persists DISABLED, so the tool catalog cannot have changed — only the registration
		// list needs refreshing. Invalidating the catalog here would force every tool picker to refetch for no
		// reason, so create is intentionally list-only.
		onSuccess: () => invalidateServersList(queryClient),
	});
}

export interface UpdateMcpServerVariables {
	id: string;
	request: SaveMcpServerRequestDto;
}

export function useUpdateMcpServer() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: ({ id, request }: UpdateMcpServerVariables) => updateMcpServer(id, request),
		onSuccess: () => invalidateServersAndCatalog(queryClient),
	});
}

export function useDeleteMcpServer() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: (id: string) => deleteMcpServer(id),
		onSuccess: () => invalidateServersAndCatalog(queryClient),
	});
}

export interface SetMcpServerEnabledVariables {
	id: string;
	enabled: boolean;
}

export function useSetMcpServerEnabled() {
	const queryClient = useQueryClient();

	return useMutation({
		mutationFn: ({ id, enabled }: SetMcpServerEnabledVariables) => setMcpServerEnabled(id, enabled),
		// Enabling/disabling changes the live tool catalog (connect/disconnect → tools appear/disappear), so the
		// catalog cache must be invalidated alongside the registration list.
		onSuccess: () => invalidateServersAndCatalog(queryClient),
	});
}

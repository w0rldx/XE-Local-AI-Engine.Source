import type { CreateMcpServerResponse, SetMcpServerEnabledResponse, UpdateMcpServerResponse } from "@/core/api/generated";
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

// Live discovered tools + connection status for one registered server; null disables the query. The endpoint reads the
// connection manager's in-memory snapshot (it never connects), so the status column asks once per enabled row and
// keeps the answer for a while; only a server still mid-connect is re-asked until it settles.
const TOOLS_STALE_TIME_MS = 30_000;
const CONNECTING_REFETCH_MS = 2_000;

export function useMcpServerTools(id: string | null) {
	return useQuery({
		...withResponseValidation(getMcpServerToolsOptions({ path: { mcpServerId: id ?? "" } })),
		enabled: id !== null,
		staleTime: TOOLS_STALE_TIME_MS,
		refetchInterval: (query) => (query.state.data?.status === "connecting" ? CONNECTING_REFETCH_MS : false),
		select: toMcpServerToolsView,
	});
}

function invalidateServersList(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	return queryClient.invalidateQueries({ queryKey: listMcpServersQueryKey() });
}

// Every server's live status, whatever its id: the generated key is [{ _id, path }], so match on the discriminator.
function isServerToolsQuery(query: { queryKey: readonly unknown[] }): boolean {
	return (query.queryKey[0] as { _id?: unknown } | undefined)?._id === "getMcpServerTools";
}

async function invalidateServersAndCatalog(queryClient: ReturnType<typeof useQueryClient>): Promise<void> {
	await Promise.all([
		invalidateServersList(queryClient),
		queryClient.invalidateQueries({ queryKey: toolCatalogQueryKeys.all() }),
		queryClient.invalidateQueries({ predicate: isServerToolsQuery }),
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

// Reconnect = enable an already-enabled server: the backend treats that as "drop and re-open this server's session"
// without a store write or Version bump.
export function useReconnectMcpServer() {
	const queryClient = useQueryClient();

	const options = withResponseValidation(setMcpServerEnabledMutation());

	return useMutation({
		mutationFn: (id: string): Promise<SetMcpServerEnabledResponse> =>
			options.mutationFn?.(
				{ path: { mcpServerId: id }, body: { enabled: true } },
				undefined as never,
			) as Promise<SetMcpServerEnabledResponse>,
		onSuccess: () => invalidateServersAndCatalog(queryClient),
	});
}

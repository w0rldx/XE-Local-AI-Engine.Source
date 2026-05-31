import { useQuery } from "@tanstack/react-query";

import { listToolCatalog } from "@/features/tools/api/ToolCatalogApi";
import { toolCatalogQueryKeys } from "@/features/tools/queries/ToolCatalogQueryKeys";

// Server state for the dynamic tool catalog (dynamic tool-catalog): node built-ins plus the tools discovered from enabled
// MCP servers. The read wires the TanStack Query AbortSignal into axios (per repo React standards). This hook
// is the single catalog source the tool pickers (agent form, chat overview, tools page) consume — it replaces
// the static localToolCatalog const. Mutating the MCP server set (the run gateway CRUD) invalidates this query so the
// pickers re-fetch the new catalog.
export function useToolCatalog() {
	return useQuery({
		queryKey: toolCatalogQueryKeys.list(),
		queryFn: ({ signal }) => listToolCatalog({ signal }),
	});
}

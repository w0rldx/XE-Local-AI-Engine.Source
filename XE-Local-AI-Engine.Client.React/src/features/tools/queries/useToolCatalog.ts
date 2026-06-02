import { useQuery } from "@tanstack/react-query";

import { getToolCatalogOptions } from "@/core/api/generated/@tanstack/react-query.gen";
import { withResponseValidation } from "@/core/api/ResponseValidation";
import { toToolCatalogEntry } from "@/features/tools/models/ToolCatalogMappers";

// Server state for the dynamic tool catalog (dynamic tool-catalog): node built-ins plus the tools discovered from enabled
// MCP servers. The read uses the generated hey-api `getToolCatalogOptions()` (which wires the shared axios instance +
// TanStack Query AbortSignal automatically) wrapped in withResponseValidation, plus a TanStack `select` that maps the
// optional-field generated response into the stricter domain view-model. This hook is the single catalog source the
// tool pickers (agent form, chat overview, tools page) consume. Mutating the MCP server set (the run gateway CRUD)
// invalidates this query (via toolCatalogQueryKeys.all(), the generated query-key matcher) so the pickers re-fetch.
export function useToolCatalog() {
	return useQuery({
		...withResponseValidation(getToolCatalogOptions()),
		select: (data) => (data.tools ?? []).map(toToolCatalogEntry),
	});
}

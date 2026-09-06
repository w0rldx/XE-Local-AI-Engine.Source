import { createFileRoute, redirect } from "@tanstack/react-router";
import { useCallback } from "react";
import { z } from "zod";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { type GraphWorkflowSelection, graphWorkflowTabs } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { GraphWorkflowsPage } from "@/features/graphWorkflows/pages/GraphWorkflowsPage";

// One route, two modes: with `runId` set the page shows a run of the pinned graph, without it the editor. All four
// selections are search params rather than path segments, so a view is linkable and a reload lands back on it. The
// `tab` union comes from the feature's own model, so adding a tab there needs no edit here.
const graphWorkflowsSearchSchema = z.object({
	definitionId: z.string().optional(),
	runId: z.string().optional(),
	nodeKey: z.string().optional(),
	tab: z.enum(graphWorkflowTabs).optional(),
});

export const Route = createFileRoute("/_layout/graph-workflows")({
	// Capability gate: Graph Workflows ships OFF (S4 flips it), so navigating here redirects home, matching the nav
	// child being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.graphWorkflows) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	validateSearch: graphWorkflowsSearchSchema,
	component: GraphWorkflowsRoute,
});

// Thin router adapter: GraphWorkflowsPage stays router-free (it is rendered directly in unit tests), so the search
// params are read here and handed down as props.
function GraphWorkflowsRoute() {
	const search = Route.useSearch();
	const navigate = Route.useNavigate();

	const handleSelectionChange = useCallback(
		(next: GraphWorkflowSelection) => {
			// `replace` so following a run does not push a history entry per node click — Back should leave the page, not
			// walk backwards through node selections. Best-effort: a failed search-param update must not break the page.
			navigate({ search: next, replace: true }).catch(() => undefined);
		},
		[navigate],
	);

	return <GraphWorkflowsPage selection={search} onSelectionChange={handleSelectionChange} />;
}

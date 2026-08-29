import { createFileRoute, redirect } from "@tanstack/react-router";
import { useCallback } from "react";
import { z } from "zod";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { devWorkflowDetailTabs } from "@/features/devWorkflows/models/DevWorkflowModels";
import { type DevWorkflowDetailSelection, DevWorkflowDetailPage } from "@/features/devWorkflows/pages/DevWorkflowDetailPage";

// The three pieces of selection that must survive a reload and be shareable as a URL: which run is displayed, which
// node-run the panel is on, and which tab is open. One `tab` param serves both tab strips — `graph`/`nodes` for the
// centre pane, `artifacts`/`events` for the side pane — and the union comes from the feature's model, so the schema
// follows it without editing here.
const devWorkflowDetailSearchSchema = z.object({
	run: z.string().optional(),
	node: z.string().optional(),
	tab: z.enum(devWorkflowDetailTabs).optional(),
});

export const Route = createFileRoute("/_layout/development-workflows/$workItemId")({
	beforeLoad: () => {
		if (!nodeCapabilities.devWorkflows) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	validateSearch: devWorkflowDetailSearchSchema,
	component: DevWorkflowDetailRoute,
});

// Thin router adapter: DevWorkflowDetailPage stays router-free (it is rendered directly in unit tests), so the search
// params are read here and handed down as props.
function DevWorkflowDetailRoute() {
	const { workItemId } = Route.useParams();
	const search = Route.useSearch();
	const navigate = Route.useNavigate();

	const handleSelectionChange = useCallback(
		(next: DevWorkflowDetailSelection) => {
			// `replace` so following a run does not push a history entry per node click — Back should leave the page,
			// not walk backwards through node selections. Best-effort: a failed search-param update must not break the
			// page, since every node is still reachable from the table.
			navigate({ search: next, replace: true }).catch(() => undefined);
		},
		[navigate],
	);

	return <DevWorkflowDetailPage workItemId={workItemId} selection={search} onSelectionChange={handleSelectionChange} />;
}

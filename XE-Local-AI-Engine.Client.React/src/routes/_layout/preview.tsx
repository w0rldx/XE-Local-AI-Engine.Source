import { createFileRoute, redirect } from "@tanstack/react-router";
import { useCallback } from "react";
import { z } from "zod";

import { nodeCapabilities, nodeRoutePaths } from "@/capabilities/NodeCapabilities";
import { PreviewPage } from "@/features/preview/pages/PreviewPage";

// `runId` is the ONE piece of run state that has to survive a page reload. Open Canvas runs live on the node, not in
// the browser, and the server keeps a seq-numbered replay log for late subscribers — but before this the runId existed
// only in page state, so a reload abandoned the run: unreachable, uncancellable, and (parked on a Pause node, which is
// deliberately exempt from the idle and wall-clock sweeps) holding its concurrency slot until the node restarted.
const previewSearchSchema = z.object({
	runId: z.string().optional(),
});

export const Route = createFileRoute("/_layout/preview")({
	// Capability gate (Open Canvas preview): when preview is off the route is hidden — navigating to it redirects
	// home, matching the nav link being filtered out of NavigationMenuData.
	beforeLoad: () => {
		if (!nodeCapabilities.preview) {
			throw redirect({ to: nodeRoutePaths.home });
		}
	},
	validateSearch: previewSearchSchema,
	component: PreviewRoute,
});

// Thin router adapter: PreviewPage itself stays router-free (it is rendered directly in unit tests), so the search
// param is read here and handed down as a prop.
function PreviewRoute() {
	const { runId } = Route.useSearch();
	const navigate = Route.useNavigate();

	const handleRunIdChange = useCallback(
		(next: string | null) => {
			// `replace` so following a run does not push a history entry per execute — Back should leave the page, not
			// walk backwards through run ids.
			// Best-effort: a failed search-param update must not break the page (the run is still reachable from the
			// runs panel), so the rejection is swallowed rather than surfaced.
			navigate({ search: next === null ? {} : { runId: next }, replace: true }).catch(() => undefined);
		},
		[navigate],
	);

	return <PreviewPage routeRunId={runId ?? null} onRouteRunIdChange={handleRunIdChange} />;
}

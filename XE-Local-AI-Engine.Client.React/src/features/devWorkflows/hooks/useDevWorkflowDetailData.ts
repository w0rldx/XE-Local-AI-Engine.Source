// The detail page's data half: the work item, the run selected under it, and the four feeds that hang off that run.
// One hook because they are one dependency chain — the work item names the run, the run's high-water mark seeds the
// event cursor, and a terminally failed work-item request must stop every feed below it.

import { useState } from "react";

import { useDevWorkflowRunHub } from "@/features/devWorkflows/hooks/useDevWorkflowRunHub";
import { isActiveDevWorkflowRunStatus, toDevWorkflowRunStatus } from "@/features/devWorkflows/models/DevWorkflowModels";
import {
	type DevWorkflowEventsAnchor,
	devWorkflowEventsAnchorParam,
	useDecideDevWorkflowNodeRun,
	useDeleteDevWorkflowWorkItem,
	useDevWorkflowArtifacts,
	useDevWorkflowDefinitions,
	useDevWorkflowNodeRun,
	useDevWorkflowRun,
	useDevWorkflowRunEvents,
	useDevWorkflowRunLifecycle,
	useDevWorkflowWorkItem,
	useStartDevWorkflowRun,
} from "@/features/devWorkflows/queries/useDevWorkflows";

/**
 * @param enabled This node's `DevWorkflows:Enabled` answer. False gates every request below, because on a disabled
 *   node the whole family answers a bodyless 404 and the page reports that state itself instead.
 */
export function useDevWorkflowDetailData(
	workItemId: string,
	selection: { readonly run?: string; readonly node?: string },
	enabled: boolean,
) {
	const [eventsAnchor, setEventsAnchor] = useState<DevWorkflowEventsAnchor>("newest");

	const workItemQuery = useDevWorkflowWorkItem(workItemId, { enabled });
	// Absent `?run=` means the latest run; an explicit one renders a historical run from its OWN pinned graph snapshot.
	const runId = selection.run ?? workItemQuery.data?.latestRunId ?? undefined;
	// Passing no run id keeps the hub idle, which is the seam it already has for "nothing to subscribe to yet". Needed
	// explicitly because `?run=` can name a run from the URL even on a node that serves none of this.
	const live = useDevWorkflowRunHub(enabled ? runId : undefined, workItemId);

	// Start every feed in parallel with the work-item request, but once that authoritative request has terminally
	// failed, stop subordinate work even if a failed hub subscription has enabled fallback polling.
	const feedsEnabled = enabled && !workItemQuery.isError;
	const poll = { pollIntervalMs: feedsEnabled ? live.pollIntervalMs : undefined, enabled: feedsEnabled };

	const runQuery = useDevWorkflowRun(runId, poll);
	const nodeRunQuery = useDevWorkflowNodeRun(runId, selection.node, poll);
	// The feed opens on the newest events and needs the run's high-water mark to compute that cursor. `?tab=`
	// carries no anchor: which end of a log you are reading is a scroll position, not a shareable view of the run.
	const eventsQuery = useDevWorkflowRunEvents(runId, runQuery.data?.lastSequence, { ...poll, anchor: eventsAnchor });
	// The same cursor the feed opened on. Passed down so the tab can say when a live run crossed a page boundary and
	// took the older pages with it, instead of them disappearing without a word.
	const eventsAnchorParam = devWorkflowEventsAnchorParam(runQuery.data?.lastSequence, eventsAnchor);
	const artifactsQuery = useDevWorkflowArtifacts(runId, poll);
	const definitionsQuery = useDevWorkflowDefinitions({ enabled });
	const lifecycle = useDevWorkflowRunLifecycle(runId, workItemId);
	const decide = useDecideDevWorkflowNodeRun(runId, workItemId);
	const startRun = useStartDevWorkflowRun();
	const deleteWorkItem = useDeleteDevWorkflowWorkItem();

	const run = runQuery.data;
	// The hub snapshot paints the status before the run query lands; once it has, the query is the authority.
	const runStatus = toDevWorkflowRunStatus(run?.status ?? live.status);
	const nodes = run?.nodes ?? [];
	const pendingDecisionCount = run?.pendingDecisionCount ?? live.pendingDecisionCount ?? 0;
	const blockingGateNodeRunId = run?.blockingGateNodeRunId ?? live.blockingGateNodeRunId ?? undefined;
	// One live run per work item, so a second start is refused with a 409. The control is simply not offered — and
	// the question is asked of the WORK ITEM's runs, not the selected one: viewing a terminal historical run under a
	// newer live run offered a Start that could only ever 409. Same rows the summary panel lists, so what the operator
	// sees and what the control believes cannot disagree.
	const canStartRun = !(workItemQuery.data?.runs ?? []).some((summary) =>
		isActiveDevWorkflowRunStatus(toDevWorkflowRunStatus(summary.status)),
	);

	return {
		workItemQuery,
		runId,
		live,
		nodeRunQuery,
		eventsQuery,
		eventsAnchor,
		setEventsAnchor,
		eventsAnchorParam,
		artifactsQuery,
		definitionsQuery,
		lifecycle,
		decide,
		startRun,
		deleteWorkItem,
		run,
		runStatus,
		nodes,
		pendingDecisionCount,
		blockingGateNodeRunId,
		canStartRun,
	};
}

import { useMemo } from "react";

import { comparePlaybookActions, type PlaybookAction } from "@/features/agents/models/PlaybookActionModels";
import type { PlaybookMonitor, PlaybookMonitorItem } from "@/features/agents/models/PlaybookMonitorModels";

/** Editor target: "create" a new action or "edit" an existing one by id. null = editor closed. */
export type PlaybookEditorTarget = { mode: "create" } | { mode: "edit"; id: string } | null;

/**
 * Everything the governance panel derives from the two independent reads (the action list and the monitor) plus the
 * open editor. Kept out of the panel so the panel body is markup: none of it mutates, and each value is memoised on
 * the query result it is derived from.
 */
export function usePlaybookPanelDerivedState(
	actions: readonly PlaybookAction[] | undefined,
	monitor: PlaybookMonitor | undefined,
	editorTarget: PlaybookEditorTarget,
) {
	// Manual governance: the existing Enabled/Disabled actions (and any unknown state that degraded to Disabled).
	// Suggested actions are the analysis-proposed proposals awaiting human review and render in their own section.
	const orderedActions = useMemo(
		() => [...(actions ?? [])].filter((action) => action.state !== "Suggested").sort(comparePlaybookActions),
		[actions],
	);

	const suggestedActions = useMemo(
		() => [...(actions ?? [])].filter((action) => action.state === "Suggested").sort(comparePlaybookActions),
		[actions],
	);

	const editingAction = useMemo(() => {
		if (editorTarget?.mode !== "edit") {
			return undefined;
		}
		// Edit can target a manual action or a Suggested proposal (operators may tweak a proposal before approving).
		return (
			orderedActions.find((action) => action.id === editorTarget.id) ??
			suggestedActions.find((action) => action.id === editorTarget.id)
		);
	}, [editorTarget, orderedActions, suggestedActions]);

	// Next priority for a brand-new action: one past the current max so it sorts at the end of the list.
	const nextPriority = useMemo(
		() => orderedActions.reduce((max, action) => Math.max(max, action.priority), -1) + 1,
		[orderedActions],
	);

	// The number of currently Enabled actions (the cohort under monitoring + the count the relevance
	// gate / cap indicator reason about). Suggested/Disabled/Archived are excluded.
	const enabledCount = useMemo(() => orderedActions.filter((action) => action.state === "Enabled").length, [orderedActions]);

	// Index the monitoring signals by actionId so each Enabled row can join its signal in O(1). An
	// action with no monitor item (no enable clock yet, or the read failed) simply renders the neutral "no signal".
	const monitorByActionId = useMemo(() => {
		const map = new Map<string, PlaybookMonitorItem>();
		for (const item of monitor?.items ?? []) {
			map.set(item.actionId, item);
		}
		return map;
	}, [monitor]);

	// The relevance-retrieval config. When more actions are Enabled than the threshold, injection is
	// gated to the top-K most relevant per turn; the banner below surfaces that with the live numbers.
	const retrieval = monitor?.retrieval ?? null;

	return {
		orderedActions,
		suggestedActions,
		editingAction,
		nextPriority,
		enabledCount,
		monitorByActionId,
		retrieval,
		showRelevanceBanner: retrieval !== null && enabledCount > retrieval.threshold,
	};
}

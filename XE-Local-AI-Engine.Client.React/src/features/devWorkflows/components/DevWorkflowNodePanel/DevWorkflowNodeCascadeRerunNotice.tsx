import { Alert } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { devWorkflowAttemptEventTypes, devWorkflowRoutedDetail } from "@/features/devWorkflows/models/DevWorkflowAttempts";
import type {
	DevWorkflowNodeRunDetailResponse,
	DevWorkflowRunEventResponse,
	DevWorkflowRunResponse,
} from "@/features/devWorkflows/models/DevWorkflowModels";

/**
 * Why completed work went back to `Pending` (C45/X9). A fix loop resets a whole cascade, so a node that had succeeded
 * is put back to run again — and without this it simply un-completes with no account of itself, which reads as the
 * module losing work.
 *
 * The evidence is `node.retry.routed`, which B4 emits against the node that FAILED, naming the target the loop routed
 * back to. This node's own log says only that it was re-scheduled, so the two are joined on sequence: the routed event
 * immediately preceding this node's latest reset is the round that reset it. A routed event this node's own row
 * produced is not a cascade — it is this node failing and retrying itself, which the attempts list already says.
 */
export function DevWorkflowNodeCascadeRerunNotice({
	nodeRun,
	nodeEvents,
	events,
	run,
}: {
	readonly nodeRun: DevWorkflowNodeRunDetailResponse;
	readonly nodeEvents: readonly DevWorkflowRunEventResponse[];
	readonly events: readonly DevWorkflowRunEventResponse[];
	readonly run?: DevWorkflowRunResponse;
}) {
	const { t } = useTranslation();
	const latestReset = nodeEvents.filter((event) => event.eventType === devWorkflowAttemptEventTypes.retryScheduled).at(-1);
	if (!latestReset) {
		return null;
	}

	// Only a routed event that names THIS node as its target explains this node's reset. Proximity alone does not: a
	// same-node retry writes `node.retry.scheduled` with no routed event of its own, so the newest routed event
	// anywhere in the run sits at-or-before it and would be read as the cause — under C2's N parallel subtrees that is
	// the ordinary case. The cost is silence for a node reset as a DESCENDANT of the routed target rather than as the
	// target itself: no banner where one would have been useful, which is the side to be wrong on.
	const routed = events
		.filter(
			(event) =>
				event.eventType === devWorkflowAttemptEventTypes.retryRouted &&
				(event.sequence ?? 0) <= (latestReset.sequence ?? 0) &&
				devWorkflowRoutedDetail(event.detailJson).to === nodeRun.nodeKey,
		)
		.toSorted((left, right) => (left.sequence ?? 0) - (right.sequence ?? 0))
		.at(-1);
	const failedNodeKey = devWorkflowRoutedDetail(routed?.detailJson).from;
	if (!failedNodeKey || failedNodeKey === nodeRun.nodeKey) {
		return null;
	}

	const failedLabel = (run?.nodes ?? []).find((node) => node.nodeKey === failedNodeKey)?.label ?? failedNodeKey;
	return (
		<Alert color="blue" variant="light" data-testid="dev-workflow-node-cascade-rerun">
			{t(
				"pages.devWorkflows.node.cascadeRerun",
				"This node is running again because “{{node}}” failed and the run is re-doing the work that depended on it.",
				{ node: failedLabel },
			)}
		</Alert>
	);
}

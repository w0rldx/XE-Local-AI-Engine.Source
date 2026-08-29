import type { MantineColor } from "@mantine/core";
import { useReducedMotion } from "framer-motion";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import {
	type DevWorkflowNodeStatus,
	type DevWorkflowRunStatus,
	type DevWorkflowWorkItemStatus,
	isDevWorkflowNodeInProgress,
} from "@/features/devWorkflows/models/DevWorkflowModels";

// Colour maps only — the pill itself is the shared StatusBadge, exactly as WorkSessionStatusBadge does it.

const runStatusColors: Record<DevWorkflowRunStatus, MantineColor> = {
	Pending: "gray",
	Running: "blue",
	// Fire-and-forget commands: the run is still winding work down, so these read as in-flight rather than as done.
	Pausing: "yellow",
	Paused: "yellow",
	// The run has stopped and only a human restarts it. Orange reads as "act now".
	WaitingForApproval: "orange",
	Cancelling: "orange",
	Completed: "green",
	Failed: "red",
	Cancelled: "gray",
};

const nodeStatusColors: Record<DevWorkflowNodeStatus, MantineColor> = {
	Pending: "gray",
	// Yellow, and NEVER a spinner: a queued node is waiting for the agent slot another node is holding. Animating it
	// would tell the operator work is happening on a GPU that is in fact serving someone else — the exact lie O9 exists
	// to prevent.
	Queued: "yellow",
	Running: "blue",
	WaitingForApproval: "orange",
	// Y20: retries are exhausted or the failure is non-retryable. The run is stopped until a human intervenes, so this
	// is the loudest colour on the table, not the quietest.
	Blocked: "red",
	Succeeded: "green",
	Failed: "red",
	Skipped: "gray",
	Cancelled: "gray",
};

const workItemStatusColors: Record<DevWorkflowWorkItemStatus, MantineColor> = {
	Draft: "gray",
	Active: "blue",
	// Y4: a failed run maps its work item here, because it needs attention rather than being done.
	Blocked: "orange",
	Completed: "green",
	Cancelled: "gray",
};

export function DevWorkflowRunStatusBadge({ status, testId }: { status: DevWorkflowRunStatus; testId?: string }) {
	const { t } = useTranslation();
	const reduced = useReducedMotion();
	const label = t(`pages.devWorkflows.runStatus.${status}`, status);
	return (
		<StatusBadge
			color={runStatusColors[status]}
			label={label}
			inProgress={!reduced && (status === "Running" || status === "Pausing" || status === "Cancelling")}
			aria-label={label}
			data-testid={testId ?? "dev-workflow-run-status-badge"}
		/>
	);
}

export function DevWorkflowNodeStatusBadge({ status, testId }: { status: DevWorkflowNodeStatus; testId?: string }) {
	const { t } = useTranslation();
	// The repo's motion-sensitivity convention (ToolCallCard, ThoughtsSection, StreamCaret): no animation when the
	// operating system asks for none. Applied here rather than at each call site so every workflow badge inherits it.
	const reduced = useReducedMotion();
	const label = t(`pages.devWorkflows.nodeStatus.${status}`, status);
	return (
		<StatusBadge
			color={nodeStatusColors[status]}
			label={label}
			inProgress={!reduced && isDevWorkflowNodeInProgress(status)}
			aria-label={label}
			data-testid={testId ?? "dev-workflow-node-status-badge"}
		/>
	);
}

export function DevWorkflowWorkItemStatusBadge({ status, testId }: { status: DevWorkflowWorkItemStatus; testId?: string }) {
	const { t } = useTranslation();
	const label = t(`pages.devWorkflows.workItemStatus.${status}`, status);
	return (
		<StatusBadge color={workItemStatusColors[status]} label={label} aria-label={label} data-testid={testId} />
	);
}

import type { MantineColor } from "@mantine/core";
import { useReducedMotion } from "framer-motion";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import {
	type GraphWorkflowNodeRunStatus,
	type GraphWorkflowRunStatus,
	narrowGraphWorkflowNodeRunStatus,
	narrowGraphWorkflowRunStatus,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";

// Colour maps only — the pill itself is the shared StatusBadge, exactly as DevWorkflowStatusBadge does it. Copied
// rather than imported: features never import each other, and these are this module's own vocabularies.

const runStatusColors: Record<GraphWorkflowRunStatus, MantineColor> = {
	Pending: "gray",
	Running: "blue",
	// The run has stopped at a Pause and only a human restarts it. Orange reads as "act now".
	WaitingForApproval: "orange",
	// Accepted, not done: the live nodes are still winding down, so this is an in-flight state.
	Cancelling: "orange",
	Completed: "green",
	Failed: "red",
	Cancelled: "gray",
};

const nodeStatusColors: Record<GraphWorkflowNodeRunStatus, MantineColor> = {
	Pending: "gray",
	// Yellow, and NEVER a spinner: a queued node is waiting for the lane another node is holding. Animating it would
	// tell the operator that work is happening on a GPU that is in fact serving someone else.
	Queued: "yellow",
	Running: "blue",
	WaitingForApproval: "orange",
	Succeeded: "green",
	Failed: "red",
	Skipped: "gray",
	Cancelled: "gray",
};

export function GraphWorkflowRunStatusBadge({
	status,
	"data-testid": testId,
}: {
	readonly status: string | undefined;
	readonly "data-testid"?: string;
}) {
	const { t } = useTranslation();
	const reduced = useReducedMotion();
	const narrowed = narrowGraphWorkflowRunStatus(status);
	const label = t(`pages.graphWorkflows.runStatus.${narrowed}`, narrowed);
	return (
		<StatusBadge
			color={runStatusColors[narrowed]}
			label={label}
			inProgress={!reduced && (narrowed === "Running" || narrowed === "Cancelling")}
			aria-label={label}
			data-testid={testId ?? "graph-workflow-run-status-badge"}
		/>
	);
}

export function GraphWorkflowNodeStatusBadge({
	status,
	"data-testid": testId,
}: {
	readonly status: string | undefined;
	readonly "data-testid"?: string;
}) {
	const { t } = useTranslation();
	// The repo's motion-sensitivity convention: no animation when the operating system asks for none.
	const reduced = useReducedMotion();
	const narrowed = narrowGraphWorkflowNodeRunStatus(status);
	const label = t(`pages.graphWorkflows.nodeStatus.${narrowed}`, narrowed);
	return (
		<StatusBadge
			color={nodeStatusColors[narrowed]}
			label={label}
			inProgress={!reduced && narrowed === "Running"}
			aria-label={label}
			data-testid={testId ?? "graph-workflow-node-status-badge"}
		/>
	);
}

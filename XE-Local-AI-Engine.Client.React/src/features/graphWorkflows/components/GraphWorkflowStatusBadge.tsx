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

// Colour maps only — the pill is the shared StatusBadge, exactly as DevWorkflowStatusBadge does it. Both maps are
// `Record<vocabulary, …>`, so a member added to the vocabulary fails to compile here rather than rendering colourless.
// They stay module-private (`useComponentExportOnlyModules`): the test walks the vocabularies through a render and
// reads the colour Mantine puts on the pill, which asserts the same thing without a second public surface.

const graphWorkflowRunStatusColors: Record<GraphWorkflowRunStatus, MantineColor> = {
	Pending: "gray",
	Running: "blue",
	// The run has stopped and only a human restarts it; `Cancelling` is still winding work down. Orange reads "act now".
	WaitingForApproval: "orange",
	Cancelling: "orange",
	Completed: "green",
	Failed: "red",
	Cancelled: "gray",
};

const graphWorkflowNodeStatusColors: Record<GraphWorkflowNodeRunStatus, MantineColor> = {
	// Every node of the pinned graph is materialized at run start, so `Pending` is the common state and must stay quiet.
	Pending: "gray",
	// Yellow, and NEVER a spinner: a queued node is waiting for a slot another node holds. Animating it would tell the
	// operator work is happening on hardware that is in fact serving someone else.
	Queued: "yellow",
	Running: "blue",
	WaitingForApproval: "orange",
	Succeeded: "green",
	Failed: "red",
	Skipped: "gray",
	Cancelled: "gray",
};

interface GraphWorkflowStatusBadgeProps {
	/** The wire value, narrowed here: the generated client types every enum as a bare `string`. */
	readonly status: string | undefined;
	readonly "data-testid"?: string;
}

export function GraphWorkflowRunStatusBadge({ status, "data-testid": testId }: GraphWorkflowStatusBadgeProps) {
	const { t } = useTranslation();
	const reduced = useReducedMotion();
	const narrowed = narrowGraphWorkflowRunStatus(status);
	const label = t(`pages.graphWorkflows.runStatus.${narrowed}`, narrowed);
	return (
		<StatusBadge
			color={graphWorkflowRunStatusColors[narrowed]}
			label={label}
			inProgress={!reduced && (narrowed === "Running" || narrowed === "Cancelling")}
			aria-label={label}
			data-testid={testId ?? "graph-workflow-run-status-badge"}
		/>
	);
}

export function GraphWorkflowNodeStatusBadge({ status, "data-testid": testId }: GraphWorkflowStatusBadgeProps) {
	const { t } = useTranslation();
	// The repo's motion-sensitivity convention, applied here rather than at each call site so every badge inherits it.
	const reduced = useReducedMotion();
	const narrowed = narrowGraphWorkflowNodeRunStatus(status);
	const label = t(`pages.graphWorkflows.nodeStatus.${narrowed}`, narrowed);
	return (
		<StatusBadge
			color={graphWorkflowNodeStatusColors[narrowed]}
			label={label}
			// `WaitingForApproval` is a stop, not progress: the run is parked until a human answers.
			inProgress={!reduced && narrowed === "Running"}
			aria-label={label}
			data-testid={testId ?? "graph-workflow-node-status-badge"}
		/>
	);
}

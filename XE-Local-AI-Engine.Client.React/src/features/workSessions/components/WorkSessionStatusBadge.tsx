import type { MantineColor } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import {
	isActiveWorkSessionStatus,
	type WorkSessionStatus,
	type WorkSessionTaskStatus,
} from "@/features/workSessions/models/WorkSessionModels";

// Colour map only — the pill itself is the shared StatusBadge, exactly as BenchmarkStatusBadge does it.
const statusColors: Record<WorkSessionStatus, MantineColor> = {
	Draft: "gray",
	Running: "blue",
	Paused: "yellow",
	// The operator not answering has a cost beyond this page: a parked step holds the node's ONE invocation slot,
	// so their own chat turn waits behind it. Orange reads as "act now", not "nothing to do here".
	WaitingForInput: "orange",
	WaitingForApproval: "orange",
	Completed: "green",
	Failed: "red",
	Cancelled: "gray",
	Interrupted: "red",
};

const taskStatusColors: Record<WorkSessionTaskStatus, MantineColor> = {
	Planned: "gray",
	Active: "blue",
	Blocked: "orange",
	Done: "green",
	Dropped: "gray",
};

export function WorkSessionStatusBadge({ status, testId }: { status: WorkSessionStatus; testId?: string }) {
	const { t } = useTranslation();
	const label = t(`pages.workSessions.status.${status}`, status);
	return (
		<StatusBadge
			color={statusColors[status]}
			label={label}
			inProgress={isActiveWorkSessionStatus(status)}
			aria-label={label}
			data-testid={testId ?? "work-session-status-badge"}
		/>
	);
}

export function WorkSessionTaskStatusBadge({ status, testId }: { status: WorkSessionTaskStatus; testId?: string }) {
	const { t } = useTranslation();
	const label = t(`pages.workSessions.taskStatus.${status}`, status);
	return <StatusBadge color={taskStatusColors[status]} label={label} inProgress={status === "Active"} aria-label={label} data-testid={testId} />;
}

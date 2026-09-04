import type { MantineColor } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import type {
	IntegrationExecutionStatus,
	IntegrationSessionStatus,
} from "@/features/integrations/models/IntegrationModels";

// Colour maps only — the pill itself is the shared StatusBadge, exactly as DevWorkflowStatusBadge does it.

const executionStatusColors: Record<IntegrationExecutionStatus, MantineColor> = {
	// Admitted but not yet holding the lease. Grey: nothing is running on the GPU for this row yet.
	Accepted: "gray",
	// Yellow and never a spinner — a queued run is waiting for a lease another run is holding.
	Queued: "yellow",
	Running: "blue",
	Completed: "green",
	Failed: "red",
	Cancelled: "gray",
};

const sessionStatusColors: Record<IntegrationSessionStatus, MantineColor> = {
	Active: "blue",
	Closed: "gray",
};

interface IntegrationExecutionStatusBadgeProps {
	status: IntegrationExecutionStatus;
	"data-testid"?: string;
}

export function IntegrationExecutionStatusBadge({ status, "data-testid": testId }: IntegrationExecutionStatusBadgeProps) {
	const { t } = useTranslation();

	return (
		<StatusBadge
			label={t(`pages.integrations.executions.status.${status}`, status)}
			color={executionStatusColors[status]}
			// Only Running is genuinely in flight; Accepted and Queued are waiting, and spinning them would claim work
			// is happening on a node that is in fact serving something else.
			inProgress={status === "Running"}
			data-testid={testId}
		/>
	);
}

interface IntegrationSessionStatusBadgeProps {
	status: IntegrationSessionStatus;
	"data-testid"?: string;
}

export function IntegrationSessionStatusBadge({ status, "data-testid": testId }: IntegrationSessionStatusBadgeProps) {
	const { t } = useTranslation();

	return (
		<StatusBadge
			label={t(`pages.integrations.sessions.status.${status}`, status)}
			color={sessionStatusColors[status]}
			inProgress={false}
			data-testid={testId}
		/>
	);
}

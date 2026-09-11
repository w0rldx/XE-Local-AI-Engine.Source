import type { MantineColor } from "@mantine/core";
import { useReducedMotion } from "framer-motion";
import { useTranslation } from "react-i18next";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { type ExternalAppStatus, isExternalAppBusy } from "@/features/externalApps/models/ExternalAppModels";

// Colour map only — the pill itself is the shared StatusBadge, exactly as DevWorkflowStatusBadge does it.
const statusColors: Record<ExternalAppStatus, MantineColor> = {
	Installing: "blue",
	Starting: "blue",
	Updating: "blue",
	// Winding down rather than starting up: amber reads as "in flight, but going the other way".
	Stopping: "yellow",
	Resetting: "yellow",
	// Destructive and in flight.
	Uninstalling: "orange",
	Running: "green",
	Stopped: "gray",
	Failed: "red",
	// Orange and NEVER a spinner: the application is doing nothing at all, it stopped on its own and needs a human.
	// Animating it would say work is happening when none is.
	StoppedUnexpectedly: "orange",
};

export function ExternalAppStatusBadge({
	status,
	"data-testid": testId,
}: {
	readonly status: ExternalAppStatus;
	readonly "data-testid"?: string;
}) {
	const { t } = useTranslation();
	// The repo's motion-sensitivity convention: no animation when the operating system asks for none.
	const reduced = useReducedMotion();
	const label = t(`pages.externalApps.status.${status}`);
	return (
		<StatusBadge
			color={statusColors[status]}
			label={label}
			inProgress={!reduced && isExternalAppBusy(status)}
			aria-label={label}
			data-testid={testId ?? "external-app-status-badge"}
		/>
	);
}

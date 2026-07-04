// Lane C: "Report a problem" entry point (plan §7.6).
//
// Captures a manual snapshot (no error present) via Lane B's `captureSnapshot('manual')`, toasts the
// outcome, and notifies the caller so it can open the Diagnostics panel. Renders as a header
// ActionIcon by default or a full Button when `variant="button"`. The capture flow itself lives in
// the shared useReportProblem hook so the mobile navigation drawer can offer the same action.

import { ActionIcon, Button, Tooltip } from "@mantine/core";
import { IconBug } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { useReportProblem } from "@/features/diagnostics/hooks/useReportProblem";

export interface ReportProblemButtonProps {
	/** `icon` renders a compact ActionIcon (header bar); `button` renders a labelled Button. */
	readonly variant?: "icon" | "button";
	/** Invoked after a snapshot is captured successfully — e.g. navigate to the Diagnostics panel. */
	readonly onReported?: () => void;
}

export function ReportProblemButton({ variant = "icon", onReported }: ReportProblemButtonProps) {
	const { t } = useTranslation();
	const { report: handleReport, pending } = useReportProblem(onReported);

	if (variant === "button") {
		return (
			<Button variant="default" leftSection={<IconBug size={16} />} loading={pending} onClick={handleReport}>
				{t("diagnostics.reportProblem")}
			</Button>
		);
	}

	return (
		<Tooltip label={t("diagnostics.reportProblemTooltip")}>
			<ActionIcon
				variant="default"
				size="xl"
				radius="md"
				aria-label={t("diagnostics.reportProblem")}
				loading={pending}
				onClick={handleReport}
			>
				<IconBug size={18} />
			</ActionIcon>
		</Tooltip>
	);
}

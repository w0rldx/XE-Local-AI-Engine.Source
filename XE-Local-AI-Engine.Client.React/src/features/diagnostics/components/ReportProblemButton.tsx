// Lane C: "Report a problem" entry point (plan §7.6).
//
// Captures a manual snapshot (no error present) via Lane B's `captureSnapshot('manual')`, toasts the
// outcome, and notifies the caller so it can open the Diagnostics panel. Renders as a header
// ActionIcon by default or a full Button when `variant="button"`.

import { ActionIcon, Button, Tooltip } from "@mantine/core";
import { IconBug } from "@tabler/icons-react";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { toast } from "@/core/ui/notifications/Toast";
import { captureSnapshot } from "@/features/diagnostics/BuildSnapshot";

export interface ReportProblemButtonProps {
	/** `icon` renders a compact ActionIcon (header bar); `button` renders a labelled Button. */
	readonly variant?: "icon" | "button";
	/** Invoked after a snapshot is captured successfully — e.g. navigate to the Diagnostics panel. */
	readonly onReported?: () => void;
}

export function ReportProblemButton({ variant = "icon", onReported }: ReportProblemButtonProps) {
	const { t } = useTranslation();
	const [pending, setPending] = useState(false);

	const handleReport = async (): Promise<void> => {
		setPending(true);
		try {
			await captureSnapshot("manual");
			toast.success(t("diagnostics.reportSuccess"));
			onReported?.();
		} catch {
			toast.error(t("diagnostics.reportError"));
		} finally {
			setPending(false);
		}
	};

	if (variant === "button") {
		return (
			<Button variant="default" leftSection={<IconBug size={16} />} loading={pending} onClick={handleReport}>
				{t("diagnostics.reportProblem")}
			</Button>
		);
	}

	return (
		<Tooltip label={t("diagnostics.reportProblemTooltip")}>
			<ActionIcon variant="subtle" aria-label={t("diagnostics.reportProblem")} loading={pending} onClick={handleReport}>
				<IconBug size={18} />
			</ActionIcon>
		</Tooltip>
	);
}

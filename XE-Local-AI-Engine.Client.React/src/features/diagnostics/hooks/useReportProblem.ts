import { useState } from "react";
import { useTranslation } from "react-i18next";

import { toast } from "@/core/ui/notifications/Toast";
import { captureSnapshot } from "@/features/diagnostics/BuildSnapshot";

/**
 * Shared "report a problem" flow: captures a manual diagnostics snapshot, toasts the outcome,
 * and notifies the caller on success (e.g. to open the Diagnostics panel). Used by the desktop
 * HeaderBar's ReportProblemButton and the mobile navigation drawer.
 */
export function useReportProblem(onReported?: () => void) {
	const { t } = useTranslation();
	const [pending, setPending] = useState(false);

	const report = async (): Promise<void> => {
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

	return { report, pending } as const;
}

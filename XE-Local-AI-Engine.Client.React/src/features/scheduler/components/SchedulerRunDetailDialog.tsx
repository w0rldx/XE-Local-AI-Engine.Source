import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { ScheduledJobRunDetail } from "@/features/scheduler/components/ScheduledJobRunDetail";
import type { ScheduledJobRun } from "@/features/scheduler/models/SchedulerModels";

interface SchedulerRunDetailDialogProps {
	run: ScheduledJobRun | undefined;
	isLoading: boolean;
	error: string | undefined;
	opened: boolean;
	onClose: () => void;
}

// Read-only run-detail dialog, split out of SchedulerPage to keep the page component small. Separate from the
// editor dialog so its lifecycle (open via selectedRunId) stays independent.
export function SchedulerRunDetailDialog({ run, isLoading, error, opened, onClose }: SchedulerRunDetailDialogProps) {
	const { t } = useTranslation();

	return (
		<DialogShell
			title={t("pages.scheduler.runs.detail.title", "Run detail")}
			opened={opened}
			onClose={onClose}
			enableFullScreenToggle={false}
			data-testid="scheduler-run-detail-card"
		>
			<ScheduledJobRunDetail run={run} isLoading={isLoading} error={error} />
		</DialogShell>
	);
}

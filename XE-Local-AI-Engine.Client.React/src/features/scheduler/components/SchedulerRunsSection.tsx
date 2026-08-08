import { Stack, Title } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { ScheduledJobRunHistoryPanel } from "@/features/scheduler/components/ScheduledJobRunHistoryPanel";
import type { ScheduledJob, ScheduledJobRun, ScheduledJobRunFilters } from "@/features/scheduler/models/SchedulerModels";

interface SchedulerRunsSectionProps {
	runs: readonly ScheduledJobRun[];
	jobs: readonly ScheduledJob[];
	filters: ScheduledJobRunFilters;
	isLoading: boolean;
	isCancelling: boolean;
	error: string | undefined;
	selectedRunId: string | null;
	onFiltersChange: (filters: ScheduledJobRunFilters) => void;
	onSelectRun: (runId: string | null) => void;
	onCancelRun: (run: ScheduledJobRun) => void;
}

// Run-history card for the scheduler page. Split out of SchedulerPage to keep the page component small.
export function SchedulerRunsSection({
	runs,
	jobs,
	filters,
	isLoading,
	isCancelling,
	error,
	selectedRunId,
	onFiltersChange,
	onSelectRun,
	onCancelRun,
}: SchedulerRunsSectionProps) {
	const { t } = useTranslation();

	return (
		<div data-testid="scheduler-runs-card">
			<Stack gap="md">
				<Title order={3}>{t("pages.scheduler.runs.title", "Run history")}</Title>
				<ScheduledJobRunHistoryPanel
					runs={runs}
					jobs={jobs}
					filters={filters}
					isLoading={isLoading}
					isCancelling={isCancelling}
					error={error}
					selectedRunId={selectedRunId}
					onFiltersChange={onFiltersChange}
					onSelectRun={onSelectRun}
					onCancelRun={onCancelRun}
				/>
			</Stack>
		</div>
	);
}

import { Group, Loader, Text } from "@mantine/core";
import { useTranslation } from "react-i18next";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { ScheduledJobList } from "@/features/scheduler/components/ScheduledJobList";
import type { ScheduledJob } from "@/features/scheduler/models/SchedulerModels";

interface SchedulerJobsSectionProps {
	jobs: readonly ScheduledJob[];
	isLoading: boolean;
	error: string | undefined;
	isMutating: boolean;
	onEdit: (id: string) => void;
	onDelete: (job: ScheduledJob) => void;
	onTrigger: (job: ScheduledJob) => void;
	onToggleEnabled: (job: ScheduledJob, enabled: boolean) => void;
}

// Jobs-list card for the scheduler page: loading, error, and the list itself. Split out of SchedulerPage to keep
// the page component small. The dialog overlays this, so it stays mounted regardless of editor state.
export function SchedulerJobsSection({
	jobs,
	isLoading,
	error,
	isMutating,
	onEdit,
	onDelete,
	onTrigger,
	onToggleEnabled,
}: SchedulerJobsSectionProps) {
	const { t } = useTranslation();

	return (
		<SectionCard data-testid="scheduler-list-card">
			{isLoading ? (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.scheduler.list.loading", "Loading scheduled jobs…")}</Text>
				</Group>
			) : null}
			{error ? <InlineErrorAlert message={error} data-testid="scheduler-list-error" /> : null}
			{!isLoading && !error ? (
				<ScheduledJobList
					jobs={jobs}
					isMutating={isMutating}
					onEdit={onEdit}
					onDelete={onDelete}
					onTrigger={onTrigger}
					onToggleEnabled={onToggleEnabled}
				/>
			) : null}
		</SectionCard>
	);
}

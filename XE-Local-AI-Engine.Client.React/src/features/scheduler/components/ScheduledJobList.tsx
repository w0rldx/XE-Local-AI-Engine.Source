import { ActionIcon, Badge, Switch, Text } from "@mantine/core";
import { IconPencil, IconPlayerPlay, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { formatTimestamp } from "@/core/formatting/TimeFormatting";
import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { ResponsiveTable, type ResponsiveTableColumn } from "@/core/ui/components/ResponsiveTable/ResponsiveTable";
import type { ScheduledJob } from "@/features/scheduler/models/SchedulerModels";

interface ScheduledJobListProps {
	jobs: readonly ScheduledJob[];
	isMutating: boolean;
	onEdit: (id: string) => void;
	onDelete: (job: ScheduledJob) => void;
	onTrigger: (job: ScheduledJob) => void;
	onToggleEnabled: (job: ScheduledJob, enabled: boolean) => void;
}

// Renders the schedule summary cell: the cron expression, the interval, or the one-shot start time depending on
// the job's schedule kind. Kept inline so the table cell stays a single source of truth for the schedule shape.
function scheduleSummary(job: ScheduledJob): string {
	if (job.scheduleKind === "Cron") {
		return job.cronExpression ?? "—";
	}
	if (job.scheduleKind === "SimpleInterval") {
		return job.intervalSeconds !== null ? `${job.intervalSeconds}s` : "—";
	}
	return formatTimestamp(job.startAtUtc);
}

// Table of scheduled jobs with enable/disable, trigger, edit, and delete row actions. Pure presentation — the
// parent owns the data and the action handlers. Parameters are redacted on the wire, so the table shows only a
// "has parameters" badge, never the raw value.
export function ScheduledJobList({ jobs, isMutating, onEdit, onDelete, onTrigger, onToggleEnabled }: ScheduledJobListProps) {
	const { t } = useTranslation();

	const columns: readonly ResponsiveTableColumn<ScheduledJob>[] = [
		{
			key: "name",
			header: t("pages.scheduler.list.columns.name", "Name"),
			primary: true,
			cell: (job) => (
				<>
					<Text fw={600}>{job.displayName}</Text>
					{job.description ? (
						<Text size="xs" c="dimmed" lineClamp={1}>
							{job.description}
						</Text>
					) : null}
				</>
			),
		},
		{
			key: "kind",
			header: t("pages.scheduler.list.columns.kind", "Kind"),
			// The badge clips with an ellipsis, so a squeezed column cut it to "ON…"; a max-content floor keeps the
			// longest English and German label whole.
			cell: (job) => (
				<Badge variant="light" color="blue" style={{ minWidth: "max-content" }} data-testid={`scheduler-job-kind-${job.id}`}>
					{t(`pages.scheduler.form.scheduleKind.options.${job.scheduleKind}`, job.scheduleKind)}
				</Badge>
			),
		},
		{
			key: "schedule",
			header: t("pages.scheduler.list.columns.schedule", "Schedule"),
			cell: (job) => (
				<Text size="sm" ff="monospace" lineClamp={1}>
					{scheduleSummary(job)}
				</Text>
			),
		},
		{
			key: "parameters",
			header: t("pages.scheduler.list.columns.parameters", "Parameters"),
			cell: (job) =>
				job.hasParameters ? (
					<Badge variant="outline" color="grape" data-testid={`scheduler-job-has-parameters-${job.id}`}>
						{t("pages.scheduler.list.hasParameters", "Yes")}
					</Badge>
				) : (
					<Text size="sm" c="dimmed">
						{t("pages.scheduler.list.noParameters", "—")}
					</Text>
				),
		},
		{
			key: "enabled",
			header: t("pages.scheduler.list.columns.enabled", "Enabled"),
			cell: (job) => (
				<Switch
					size="sm"
					checked={job.enabled}
					disabled={isMutating}
					onChange={(event) => onToggleEnabled(job, event.currentTarget.checked)}
					aria-label={t("pages.scheduler.list.enabledAria", "Toggle {{name}}", { name: job.displayName })}
					data-testid={`scheduler-job-enabled-${job.id}`}
				/>
			),
		},
	];

	return (
		<ResponsiveTable
			rows={jobs}
			columns={columns}
			rowKey={(job) => job.id}
			rowTestId={(job) => `scheduler-job-row-${job.id}`}
			minWidth={820}
			data-testid="scheduler-jobs-table"
			empty={
				<EmptyState
					message={t("pages.scheduler.list.empty", "No scheduled jobs yet. Create one to get started.")}
					data-testid="scheduler-jobs-empty"
				/>
			}
			actionsHeader={t("pages.scheduler.list.columns.actions", "Actions")}
			actions={(job) => (
				<>
					<ActionIcon
						aria-label={t("pages.scheduler.list.triggerAria", "Run {{name}} now", { name: job.displayName })}
						variant="subtle"
						color="green"
						disabled={isMutating}
						onClick={() => onTrigger(job)}
						data-testid={`scheduler-job-trigger-${job.id}`}
					>
						<IconPlayerPlay size={16} />
					</ActionIcon>
					<ActionIcon
						aria-label={t("pages.scheduler.list.editAria", "Edit {{name}}", { name: job.displayName })}
						variant="subtle"
						disabled={isMutating}
						onClick={() => onEdit(job.id)}
						data-testid={`scheduler-job-edit-${job.id}`}
					>
						<IconPencil size={16} />
					</ActionIcon>
					<ActionIcon
						aria-label={t("pages.scheduler.list.deleteAria", "Delete {{name}}", { name: job.displayName })}
						variant="subtle"
						color="red"
						disabled={isMutating}
						onClick={() => onDelete(job)}
						data-testid={`scheduler-job-delete-${job.id}`}
					>
						<IconTrash size={16} />
					</ActionIcon>
				</>
			)}
		/>
	);
}

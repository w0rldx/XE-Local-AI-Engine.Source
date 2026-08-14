import { ActionIcon, Badge, Group, Switch, Table, Text } from "@mantine/core";
import { IconPencil, IconPlayerPlay, IconTrash } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
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
	if (job.startAtUtc !== null) {
		const date = new Date(job.startAtUtc);
		return Number.isNaN(date.getTime()) ? "—" : date.toLocaleString();
	}
	return "—";
}

// Table of scheduled jobs with enable/disable, trigger, edit, and delete row actions. Pure presentation — the
// parent owns the data and the action handlers. Parameters are redacted on the wire, so the table shows only a
// "has parameters" badge, never the raw value.
export function ScheduledJobList({ jobs, isMutating, onEdit, onDelete, onTrigger, onToggleEnabled }: ScheduledJobListProps) {
	const { t } = useTranslation();

	if (jobs.length === 0) {
		return (
			<EmptyState
				message={t("pages.scheduler.list.empty", "No scheduled jobs yet. Create one to get started.")}
				data-testid="scheduler-jobs-empty"
			/>
		);
	}

	return (
		<Table.ScrollContainer minWidth={820}>
			<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="scheduler-jobs-table">
				<Table.Thead>
					<Table.Tr>
						<Table.Th>{t("pages.scheduler.list.columns.name", "Name")}</Table.Th>
						<Table.Th>{t("pages.scheduler.list.columns.kind", "Kind")}</Table.Th>
						<Table.Th>{t("pages.scheduler.list.columns.schedule", "Schedule")}</Table.Th>
						<Table.Th>{t("pages.scheduler.list.columns.parameters", "Parameters")}</Table.Th>
						<Table.Th>{t("pages.scheduler.list.columns.enabled", "Enabled")}</Table.Th>
						<Table.Th>{t("pages.scheduler.list.columns.actions", "Actions")}</Table.Th>
					</Table.Tr>
				</Table.Thead>
				<Table.Tbody>
					{jobs.map((job) => (
						<Table.Tr key={job.id} data-testid={`scheduler-job-row-${job.id}`}>
							<Table.Td>
								<Text fw={600}>{job.displayName}</Text>
								{job.description ? (
									<Text size="xs" c="dimmed" lineClamp={1}>
										{job.description}
									</Text>
								) : null}
							</Table.Td>
							<Table.Td>
								<Badge variant="light" color="blue">
									{t(`pages.scheduler.form.scheduleKind.options.${job.scheduleKind}`, job.scheduleKind)}
								</Badge>
							</Table.Td>
							<Table.Td>
								<Text size="sm" ff="monospace" lineClamp={1}>
									{scheduleSummary(job)}
								</Text>
							</Table.Td>
							<Table.Td>
								{job.hasParameters ? (
									<Badge variant="outline" color="grape" data-testid={`scheduler-job-has-parameters-${job.id}`}>
										{t("pages.scheduler.list.hasParameters", "Yes")}
									</Badge>
								) : (
									<Text size="sm" c="dimmed">
										{t("pages.scheduler.list.noParameters", "—")}
									</Text>
								)}
							</Table.Td>
							<Table.Td>
								<Switch
									size="sm"
									checked={job.enabled}
									disabled={isMutating}
									onChange={(event) => onToggleEnabled(job, event.currentTarget.checked)}
									aria-label={t("pages.scheduler.list.enabledAria", "Toggle {{name}}", { name: job.displayName })}
									data-testid={`scheduler-job-enabled-${job.id}`}
								/>
							</Table.Td>
							<Table.Td>
								<Group gap="xs">
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
								</Group>
							</Table.Td>
						</Table.Tr>
					))}
				</Table.Tbody>
			</Table>
		</Table.ScrollContainer>
	);
}

import { ActionIcon, Alert, Badge, Group, Loader, Select, Stack, Table, Text } from "@mantine/core";
import { IconAlertTriangle, IconEye, IconX } from "@tabler/icons-react";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { TablePaginationFooter } from "@/core/ui/components/TablePagination/TablePaginationFooter";
import { useTablePagination } from "@/core/ui/components/TablePagination/useTablePagination";
import { formatDurationSeconds, formatTimestamp } from "@/core/formatting/TimeFormatting";
import { scheduledRunStatusColor } from "@/features/scheduler/components/SchedulerRunFormatters";
import {
	isActiveRunStatus,
	type ScheduledJob,
	type ScheduledJobRun,
	type ScheduledJobRunFilters,
	type ScheduledRunStatus,
	scheduledRunStatuses,
} from "@/features/scheduler/models/SchedulerModels";

interface ScheduledJobRunHistoryPanelProps {
	runs: readonly ScheduledJobRun[];
	jobs: readonly ScheduledJob[];
	filters: ScheduledJobRunFilters;
	isLoading: boolean;
	isCancelling: boolean;
	error?: string;
	selectedRunId: string | null;
	onFiltersChange: (filters: ScheduledJobRunFilters) => void;
	onSelectRun: (runId: string) => void;
	onCancelRun: (run: ScheduledJobRun) => void;
}

const ALL_VALUE = "__all__";

// Run-history table with job + status filters and a per-row cancel action for active runs. Pure presentation:
// the parent owns the data, the active filters, and the action handlers. Cancel is only offered while a run is
// still active (Queued/Running) — terminal runs expose only the view action. The view action opens the redacted
// detail panel owned by the parent.
export function ScheduledJobRunHistoryPanel({
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
}: ScheduledJobRunHistoryPanelProps) {
	const { t } = useTranslation();

	// Client-side pagination over the full filtered run set. The hook clamps the active page when the filters
	// narrow the list, so a server refetch never strands the operator on an out-of-range page. The chosen page
	// size persists across reloads under this storageKey.
	const pagination = useTablePagination(runs, { storageKey: "scheduler-run-history" });

	const jobData = useMemo(
		() => [
			{ value: ALL_VALUE, label: t("pages.scheduler.runs.filters.allJobs", "All jobs") },
			...jobs.map((job) => ({ value: job.id, label: job.displayName })),
		],
		[jobs, t],
	);

	const statusData = useMemo(
		() => [
			{ value: ALL_VALUE, label: t("pages.scheduler.runs.filters.allStatuses", "All statuses") },
			...scheduledRunStatuses.map((status) => ({
				value: status,
				label: t(`pages.scheduler.runs.status.${status}`, status),
			})),
		],
		[t],
	);

	const handleJobChange = (value: string | null): void => {
		onFiltersChange({ ...filters, scheduledJobId: value && value !== ALL_VALUE ? value : undefined });
	};

	const handleStatusChange = (value: string | null): void => {
		onFiltersChange({
			...filters,
			status: value && value !== ALL_VALUE ? (value as ScheduledRunStatus) : undefined,
		});
	};

	return (
		<Stack gap="md" data-testid="scheduler-run-history">
			<Group gap="sm">
				<Select
					label={t("pages.scheduler.runs.filters.job", "Job")}
					data={jobData}
					value={filters.scheduledJobId ?? ALL_VALUE}
					onChange={handleJobChange}
					data-testid="scheduler-runs-filter-job"
				/>
				<Select
					label={t("pages.scheduler.runs.filters.status", "Status")}
					data={statusData}
					value={filters.status ?? ALL_VALUE}
					onChange={handleStatusChange}
					data-testid="scheduler-runs-filter-status"
				/>
			</Group>

			{isLoading ? (
				<Group gap="sm">
					<Loader size="sm" />
					<Text c="dimmed">{t("pages.scheduler.runs.loading", "Loading run history…")}</Text>
				</Group>
			) : null}

			{error ? (
				<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="scheduler-runs-error">
					{error}
				</Alert>
			) : null}

			{!isLoading && !error && runs.length === 0 ? (
				<Text c="dimmed" data-testid="scheduler-runs-empty">
					{t("pages.scheduler.runs.empty", "No runs match the current filters.")}
				</Text>
			) : null}

			{!isLoading && !error && runs.length > 0 ? (
				<>
					<Table.ScrollContainer minWidth={760}>
						<Table striped={true} highlightOnHover={true} verticalSpacing="sm" data-testid="scheduler-runs-table">
							<Table.Thead>
								<Table.Tr>
									<Table.Th>{t("pages.scheduler.runs.columns.status", "Status")}</Table.Th>
									<Table.Th>{t("pages.scheduler.runs.columns.trigger", "Trigger")}</Table.Th>
									<Table.Th>{t("pages.scheduler.runs.columns.fired", "Fired")}</Table.Th>
									<Table.Th>{t("pages.scheduler.runs.columns.duration", "Duration")}</Table.Th>
									<Table.Th>{t("pages.scheduler.runs.columns.summary", "Summary")}</Table.Th>
									<Table.Th>{t("pages.scheduler.runs.columns.actions", "Actions")}</Table.Th>
								</Table.Tr>
							</Table.Thead>
							<Table.Tbody>
								{pagination.pageItems.map((run) => {
									const isActive = isActiveRunStatus(run.status);
									const isSelected = run.id === selectedRunId;
									return (
										<Table.Tr
											key={run.id}
											data-testid={`scheduler-run-row-${run.id}`}
											bg={isSelected ? "var(--mantine-color-blue-light)" : undefined}
										>
											<Table.Td>
												<Badge color={scheduledRunStatusColor(run.status)} variant="light">
													{t(`pages.scheduler.runs.status.${run.status}`, run.status)}
												</Badge>
											</Table.Td>
											<Table.Td>{t(`pages.scheduler.runs.trigger.${run.triggeredBy}`, run.triggeredBy)}</Table.Td>
											<Table.Td>{formatTimestamp(run.actualFireTimeUtc ?? run.scheduledFireTimeUtc)}</Table.Td>
											<Table.Td>{formatDurationSeconds(run.durationMs)}</Table.Td>
											<Table.Td>
												<Text size="sm" lineClamp={1}>
													{run.summary ?? "—"}
												</Text>
											</Table.Td>
											<Table.Td>
												<Group gap="xs">
													<ActionIcon
														aria-label={t("pages.scheduler.runs.viewAria", "View run")}
														variant="subtle"
														onClick={() => onSelectRun(run.id)}
														data-testid={`scheduler-run-view-${run.id}`}
													>
														<IconEye size={16} />
													</ActionIcon>
													{isActive ? (
														<ActionIcon
															aria-label={t("pages.scheduler.runs.cancelAria", "Cancel run")}
															variant="subtle"
															color="red"
															disabled={isCancelling}
															onClick={() => onCancelRun(run)}
															data-testid={`scheduler-run-cancel-${run.id}`}
														>
															<IconX size={16} />
														</ActionIcon>
													) : null}
												</Group>
											</Table.Td>
										</Table.Tr>
									);
								})}
							</Table.Tbody>
						</Table>
					</Table.ScrollContainer>
					<TablePaginationFooter
						page={pagination.page}
						pageCount={pagination.pageCount}
						pageSize={pagination.pageSize}
						totalItems={pagination.totalItems}
						firstItemIndex={pagination.firstItemIndex}
						lastItemIndex={pagination.lastItemIndex}
						pageSizeOptions={pagination.pageSizeOptions}
						onPageChange={pagination.setPage}
						onPageSizeChange={pagination.setPageSize}
						data-testid="scheduler-runs-pagination"
					/>
				</>
			) : null}
		</Stack>
	);
}

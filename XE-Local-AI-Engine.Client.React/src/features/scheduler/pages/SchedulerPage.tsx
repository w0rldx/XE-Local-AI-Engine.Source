import { Alert, Button, Card, Container, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCalendarClock, IconPlus } from "@tabler/icons-react";
import { useCallback, useMemo, useState } from "react";
import { useTranslation } from "react-i18next";

import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { ScheduledJobForm } from "@/features/scheduler/components/ScheduledJobForm";
import { ScheduledJobList } from "@/features/scheduler/components/ScheduledJobList";
import { ScheduledJobRunDetail } from "@/features/scheduler/components/ScheduledJobRunDetail";
import { ScheduledJobRunHistoryPanel } from "@/features/scheduler/components/ScheduledJobRunHistoryPanel";
import { useSchedulerHub } from "@/features/scheduler/hooks/useSchedulerHub";
import { toSaveScheduledJobRequest } from "@/features/scheduler/models/SchedulerMappers";
import type { ScheduledJob, ScheduledJobFormValues, ScheduledJobRun, ScheduledJobRunFilters } from "@/features/scheduler/models/SchedulerModels";
import { emptySchedulerFormValues, toSchedulerFormValues } from "@/features/scheduler/pages/SchedulerPageFormMappers";
import {
	useCancelScheduledJobRun,
	useCreateScheduledJob,
	useDeleteScheduledJob,
	useScheduledJobRun,
	useScheduledJobRuns,
	useScheduledJobs,
	useScheduledJobTemplates,
	useSetScheduledJobEnabled,
	useTriggerScheduledJob,
	useUpdateScheduledJob,
} from "@/features/scheduler/queries/useScheduler";
import { useSchedulerManagementStore } from "@/features/scheduler/stores/SchedulerManagementStore";

const errorMessage = (error: unknown, fallback: string): string => (error instanceof Error ? error.message : fallback);

export function SchedulerPage() {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	// Live invalidation from the scheduler hub: a server push refetches the jobs / runs queries.
	useSchedulerHub();

	const editorTarget = useSchedulerManagementStore((state) => state.editorTarget);
	const selectedRunId = useSchedulerManagementStore((state) => state.selectedRunId);
	const openCreate = useSchedulerManagementStore((state) => state.actions.openCreate);
	const openEdit = useSchedulerManagementStore((state) => state.actions.openEdit);
	const closeEditor = useSchedulerManagementStore((state) => state.actions.closeEditor);
	const selectRun = useSchedulerManagementStore((state) => state.actions.selectRun);

	const [runFilters, setRunFilters] = useState<ScheduledJobRunFilters>({});

	const templatesQuery = useScheduledJobTemplates();
	const jobsQuery = useScheduledJobs();
	const runsQuery = useScheduledJobRuns(runFilters, { refetchInterval: 5000 });
	const runQuery = useScheduledJobRun(selectedRunId);

	const createMutation = useCreateScheduledJob();
	const updateMutation = useUpdateScheduledJob();
	const deleteMutation = useDeleteScheduledJob();
	const enableMutation = useSetScheduledJobEnabled();
	const triggerMutation = useTriggerScheduledJob();
	const cancelMutation = useCancelScheduledJobRun();

	const jobs = useMemo(() => jobsQuery.data ?? [], [jobsQuery.data]);
	const templates = templatesQuery.data ?? [];
	const runs = runsQuery.data ?? [];

	const editingJob = useMemo(() => {
		if (editorTarget?.mode !== "edit") {
			return undefined;
		}
		return jobs.find((job) => job.id === editorTarget.id);
	}, [jobs, editorTarget]);

	const isMutating =
		createMutation.isPending ||
		updateMutation.isPending ||
		deleteMutation.isPending ||
		enableMutation.isPending ||
		triggerMutation.isPending;

	const submitError =
		createMutation.error || updateMutation.error
			? errorMessage(
					createMutation.error ?? updateMutation.error,
					t("pages.scheduler.errors.save", "Could not save the scheduled job."),
				)
			: undefined;

	const handleSubmit = useCallback(
		(values: ScheduledJobFormValues) => {
			const body = toSaveScheduledJobRequest(values);

			if (editorTarget?.mode === "edit") {
				updateMutation.mutate(
					{ path: { scheduledJobId: editorTarget.id }, body },
					{ onSuccess: () => closeEditor() },
				);
				return;
			}

			createMutation.mutate({ body }, { onSuccess: () => closeEditor() });
		},
		[closeEditor, createMutation, editorTarget, updateMutation],
	);

	const handleDelete = useCallback(
		async (job: ScheduledJob) => {
			const confirmed = await confirm({
				title: t("pages.scheduler.delete.title", "Delete scheduled job"),
				description: t("pages.scheduler.delete.description", "Delete '{{name}}'? This cannot be undone.", {
					name: job.displayName,
				}),
				confirmationText: t("common.delete", "Delete"),
				cancellationText: t("common.cancel", "Cancel"),
			});

			if (confirmed) {
				deleteMutation.mutate({ path: { scheduledJobId: job.id } });
			}
		},
		[confirm, deleteMutation, t],
	);

	const handleTrigger = useCallback(
		(job: ScheduledJob) => {
			triggerMutation.mutate({ path: { scheduledJobId: job.id } });
		},
		[triggerMutation],
	);

	const handleToggleEnabled = useCallback(
		(job: ScheduledJob, enabled: boolean) => {
			enableMutation.mutate({ id: job.id, enabled });
		},
		[enableMutation],
	);

	const handleCancelRun = useCallback(
		(run: ScheduledJobRun) => {
			cancelMutation.mutate({ path: { runId: run.id } });
		},
		[cancelMutation],
	);

	const isEditorOpen = editorTarget !== null;
	const formInitialValues = editingJob ? toSchedulerFormValues(editingJob) : emptySchedulerFormValues;

	return (
		<Container fluid={true} py="lg">
			<Stack gap="lg">
				<Group justify="space-between" align="flex-start">
					<Stack gap={4}>
						<Text size="sm" tt="uppercase" fw={700} c="dimmed">
							{t("pages.scheduler.eyebrow", "Worker Node")}
						</Text>
						<Group gap="xs" align="center">
							<IconCalendarClock size={24} />
							<Title order={2}>{t("pages.scheduler.title", "Scheduler")}</Title>
						</Group>
						<Text c="dimmed">
							{t(
								"pages.scheduler.subtitle",
								"Schedule recurring and one-off jobs on this node. Jobs are disabled until you enable them, and parameters are stored encrypted.",
							)}
						</Text>
					</Stack>
					{!isEditorOpen ? (
						<Button leftSection={<IconPlus size={16} />} onClick={openCreate} data-testid="scheduler-create-button">
							{t("pages.scheduler.createButton", "Create job")}
						</Button>
					) : null}
				</Group>

				{deleteMutation.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="scheduler-delete-error">
						{errorMessage(deleteMutation.error, t("pages.scheduler.errors.delete", "Could not delete the scheduled job."))}
					</Alert>
				) : null}

				{enableMutation.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="scheduler-enable-error">
						{errorMessage(enableMutation.error, t("pages.scheduler.errors.enable", "Could not change the job state."))}
					</Alert>
				) : null}

				{triggerMutation.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="scheduler-trigger-error">
						{errorMessage(triggerMutation.error, t("pages.scheduler.errors.trigger", "Could not trigger the job."))}
					</Alert>
				) : null}

				{cancelMutation.error ? (
					<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="scheduler-cancel-error">
						{errorMessage(cancelMutation.error, t("pages.scheduler.errors.cancel", "Could not cancel the run."))}
					</Alert>
				) : null}

				{isEditorOpen ? (
					<Card withBorder={true} radius="md" p="lg" data-testid="scheduler-editor-card">
						<Stack gap="md">
							<Title order={3}>
								{editorTarget?.mode === "edit"
									? t("pages.scheduler.editor.editTitle", "Edit scheduled job")
									: t("pages.scheduler.editor.createTitle", "Create scheduled job")}
							</Title>
							<ScheduledJobForm
								key={editorTarget?.mode === "edit" ? editorTarget.id : "create"}
								initialValues={formInitialValues}
								templates={templates}
								isEditing={editorTarget?.mode === "edit"}
								isSubmitting={createMutation.isPending || updateMutation.isPending}
								submitError={submitError}
								onSubmit={handleSubmit}
								onCancel={closeEditor}
							/>
						</Stack>
					</Card>
				) : (
					<Card withBorder={true} radius="md" p="lg">
						<Stack gap="md">
							{jobsQuery.isLoading ? (
								<Group gap="sm">
									<Loader size="sm" />
									<Text c="dimmed">{t("pages.scheduler.list.loading", "Loading scheduled jobs…")}</Text>
								</Group>
							) : null}
							{jobsQuery.error ? (
								<Alert color="red" icon={<IconAlertTriangle size={16} />} data-testid="scheduler-list-error">
									{errorMessage(jobsQuery.error, t("pages.scheduler.errors.load", "Could not load scheduled jobs."))}
								</Alert>
							) : null}
							{!jobsQuery.isLoading && !jobsQuery.error ? (
								<ScheduledJobList
									jobs={jobs}
									isMutating={isMutating}
									onEdit={openEdit}
									onDelete={handleDelete}
									onTrigger={handleTrigger}
									onToggleEnabled={handleToggleEnabled}
								/>
							) : null}
						</Stack>
					</Card>
				)}

				<Card withBorder={true} radius="md" p="lg" data-testid="scheduler-runs-card">
					<Stack gap="md">
						<Title order={3}>{t("pages.scheduler.runs.title", "Run history")}</Title>
						<ScheduledJobRunHistoryPanel
							runs={runs}
							jobs={jobs}
							filters={runFilters}
							isLoading={runsQuery.isLoading}
							isCancelling={cancelMutation.isPending}
							error={
								runsQuery.error
									? errorMessage(runsQuery.error, t("pages.scheduler.errors.loadRuns", "Could not load run history."))
									: undefined
							}
							selectedRunId={selectedRunId}
							onFiltersChange={setRunFilters}
							onSelectRun={selectRun}
							onCancelRun={handleCancelRun}
						/>
						{selectedRunId ? (
							<Card withBorder={true} radius="md" p="md" data-testid="scheduler-run-detail-card">
								<ScheduledJobRunDetail
									run={runQuery.data}
									isLoading={runQuery.isLoading}
									error={
										runQuery.error
											? errorMessage(runQuery.error, t("pages.scheduler.errors.loadRun", "Could not load the run."))
											: undefined
									}
								/>
							</Card>
						) : null}
					</Stack>
				</Card>
			</Stack>
		</Container>
	);
}

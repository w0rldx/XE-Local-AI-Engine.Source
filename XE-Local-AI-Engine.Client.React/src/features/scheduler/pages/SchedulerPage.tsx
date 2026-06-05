import { Alert, Button, Container, Group, Loader, Stack, Text, Title } from "@mantine/core";
import { IconAlertTriangle, IconCalendarClock, IconDeviceFloppy, IconPlus, IconX } from "@tabler/icons-react";
import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogShell } from "@/core/ui/components/DialogShell/DialogShell";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { useUnsavedChangesGuard } from "@/core/ui/hooks/useUnsavedChangesGuard";
import { toast } from "@/core/ui/notifications/Toast";
import { ScheduledJobForm, type ScheduledJobFormHandle } from "@/features/scheduler/components/ScheduledJobForm";
import { ScheduledJobList } from "@/features/scheduler/components/ScheduledJobList";
import { ScheduledJobRunDetail } from "@/features/scheduler/components/ScheduledJobRunDetail";
import { ScheduledJobRunHistoryPanel } from "@/features/scheduler/components/ScheduledJobRunHistoryPanel";
import { useSchedulerHub } from "@/features/scheduler/hooks/useSchedulerHub";
import { toSaveScheduledJobRequest } from "@/features/scheduler/models/SchedulerMappers";
import type {
	ScheduledJob,
	ScheduledJobFormValues,
	ScheduledJobRun,
	ScheduledJobRunFilters,
} from "@/features/scheduler/models/SchedulerModels";
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

	// STUCK-BUG FIX: reset both the editor and the selected run on unmount so navigating away and
	// back does not reopen the editor / run-detail panel from stale Zustand state.
	useEffect(() => {
		return () => {
			closeEditor();
			selectRun(null);
		};
		// Stable store action refs — safe to omit from the dep array per Zustand conventions.
		// eslint-disable-next-line react-hooks/exhaustive-deps
	}, [closeEditor, selectRun]);

	const [runFilters, setRunFilters] = useState<ScheduledJobRunFilters>({});

	// Dirty tracking: the form calls onDirtyChange whenever its internal values differ from initialValues.
	const [isFormDirty, setIsFormDirty] = useState(false);

	// Block in-app navigation while the form has unsaved edits.
	useUnsavedChangesGuard({ isDirty: isFormDirty });

	// Ref to the form's imperative handle — used by the footer Save button to drive
	// validation without coupling the footer to internal form state (MED-3).
	const formRef = useRef<ScheduledJobFormHandle>(null);

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

	// MED-1: close helper that also clears isFormDirty so useUnsavedChangesGuard does
	// not fire a spurious "unsaved changes" prompt when the operator navigates right
	// after a successful save.
	const closeEditorClean = useCallback(() => {
		setIsFormDirty(false);
		closeEditor();
	}, [closeEditor]);

	const handleSubmit = useCallback(
		(values: ScheduledJobFormValues) => {
			const body = toSaveScheduledJobRequest(values);

			if (editorTarget?.mode === "edit") {
				updateMutation.mutate({ path: { scheduledJobId: editorTarget.id }, body }, { onSuccess: () => closeEditorClean() });
				return;
			}

			createMutation.mutate({ body }, { onSuccess: () => closeEditorClean() });
		},
		[closeEditorClean, createMutation, editorTarget, updateMutation],
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
				deleteMutation.mutate(
					{ path: { scheduledJobId: job.id } },
					{ onError: (error) => toast.error(errorMessage(error, t("pages.scheduler.errors.delete", "Could not delete the scheduled job."))) },
				);
			}
		},
		[confirm, deleteMutation, t],
	);

	const handleTrigger = useCallback(
		(job: ScheduledJob) => {
			triggerMutation.mutate(
				{ path: { scheduledJobId: job.id } },
				{ onError: (error) => toast.error(errorMessage(error, t("pages.scheduler.errors.trigger", "Could not trigger the job."))) },
			);
		},
		[triggerMutation, t],
	);

	const handleToggleEnabled = useCallback(
		(job: ScheduledJob, enabled: boolean) => {
			enableMutation.mutate(
				{ id: job.id, enabled },
				{ onError: (error) => toast.error(errorMessage(error, t("pages.scheduler.errors.enable", "Could not change the job state."))) },
			);
		},
		[enableMutation, t],
	);

	const handleCancelRun = useCallback(
		(run: ScheduledJobRun) => {
			cancelMutation.mutate(
				{ path: { runId: run.id } },
				{ onError: (error) => toast.error(errorMessage(error, t("pages.scheduler.errors.cancel", "Could not cancel the run."))) },
			);
		},
		[cancelMutation, t],
	);

	const isEditorOpen = editorTarget !== null;
	const formInitialValues = editingJob ? toSchedulerFormValues(editingJob) : emptySchedulerFormValues;

	const editorTitle =
		editorTarget?.mode === "edit"
			? t("pages.scheduler.editor.editTitle", "Edit scheduled job")
			: t("pages.scheduler.editor.createTitle", "Create scheduled job");

	const isSubmitting = createMutation.isPending || updateMutation.isPending;

	// Unified close path: both the title-bar X and the footer Cancel route through here.
	// When the form is dirty a confirmation is required; when clean (or after a confirmed
	// discard) the editor closes immediately via closeEditorClean.
	const requestCloseEditor = useCallback(async () => {
		if (!isFormDirty) {
			closeEditorClean();
			return;
		}
		const shouldDiscard = await confirm({
			title: t("components.dialogShell.unsavedTitle", "Unsaved changes"),
			description: t("components.dialogShell.unsavedDescription", "You have unsaved changes. Discard them and leave?"),
			confirmationText: t("common.discard", "Discard"),
			cancellationText: t("common.keepEditing", "Keep editing"),
		});
		if (shouldDiscard) {
			closeEditorClean();
		}
	}, [isFormDirty, closeEditorClean, confirm, t]);

	// Footer for the editor dialog: Cancel + Save always visible regardless of form length.
	const editorFooter = (
		<>
			<Button
				variant="subtle"
				leftSection={<IconX size={16} />}
				onClick={requestCloseEditor}
				disabled={isSubmitting}
				data-testid="scheduler-form-cancel"
			>
				{t("common.cancel", "Cancel")}
			</Button>
			<Button
				leftSection={<IconDeviceFloppy size={16} />}
				onClick={() => formRef.current?.submit()}
				loading={isSubmitting}
				data-testid="scheduler-form-submit"
			>
				{t("common.save", "Save")}
			</Button>
		</>
	);

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
					<Button leftSection={<IconPlus size={16} />} onClick={openCreate} data-testid="scheduler-create-button">
						{t("pages.scheduler.createButton", "Create job")}
					</Button>
				</Group>

				{/* Editor dialog: replaces the inline card. Both the title-bar X and the footer
				    Cancel route through requestCloseEditor so a dirty-state confirm is shown
				    consistently from either path. Overlay and escape are disabled while dirty
				    so accidental dismissal never silently discards work. zIndex 300 sits below
				    the ConfirmProvider's 400 so confirmation dialogs always render on top. */}
				<DialogShell
					title={editorTitle}
					opened={isEditorOpen}
					onClose={requestCloseEditor}
					closeOnClickOutside={!isFormDirty}
					closeOnEscape={!isFormDirty}
					footer={editorFooter}
					zIndex={300}
					data-testid="scheduler-editor-card"
				>
					<ScheduledJobForm
						ref={formRef}
						key={editorTarget?.mode === "edit" ? editorTarget.id : "create"}
						initialValues={formInitialValues}
						templates={templates}
						isEditing={editorTarget?.mode === "edit"}
						isSubmitting={isSubmitting}
						submitError={submitError}
						onSubmit={handleSubmit}
						onCancel={requestCloseEditor}
						onDirtyChange={setIsFormDirty}
					/>
				</DialogShell>

				{/* Job list — always visible (dialog overlays it). */}
				<div data-testid="scheduler-list-card">
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
				</div>

				<div data-testid="scheduler-runs-card">
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
					</Stack>
				</div>

				{/* Run-detail dialog: read-only, separate from the editor dialog. */}
				<DialogShell
					title={t("pages.scheduler.runs.detail.title", "Run detail")}
					opened={selectedRunId !== null}
					onClose={() => selectRun(null)}
					enableFullScreenToggle={false}
					data-testid="scheduler-run-detail-card"
				>
					<ScheduledJobRunDetail
						run={runQuery.data}
						isLoading={runQuery.isLoading}
						error={
							runQuery.error
								? errorMessage(runQuery.error, t("pages.scheduler.errors.loadRun", "Could not load the run."))
								: undefined
						}
					/>
				</DialogShell>
			</Stack>
		</Container>
	);
}

import { useCallback } from "react";
import { useTranslation } from "react-i18next";

import { apiErrorMessage } from "@/core/api/errors/ApiErrorMessage";
import { useConfirm } from "@/core/ui/hooks/useConfirm";
import { toast } from "@/core/ui/notifications/Toast";
import type { ScheduledJob, ScheduledJobRun } from "@/features/scheduler/models/SchedulerModels";
import {
	useCancelScheduledJobRun,
	useDeleteScheduledJob,
	useSetScheduledJobEnabled,
	useTriggerScheduledJob,
} from "@/features/scheduler/queries/useScheduler";

export interface SchedulerJobMutations {
	/** True while a row action that changes the jobs list is in flight, so the list can disable its own controls. */
	isMutating: boolean;
	isCancelling: boolean;
	remove: (job: ScheduledJob) => Promise<void>;
	trigger: (job: ScheduledJob) => void;
	setEnabled: (job: ScheduledJob, enabled: boolean) => void;
	cancelRun: (run: ScheduledJobRun) => void;
}

/**
 * The row actions on the scheduler list. Each one is fire-and-report: the mutation invalidates its own caches, so the
 * only thing left to do here is surface a failure as a toast. Deleting confirms first — it is the one action with no
 * undo. Creating and updating a job stay with the page: they are the editor dialog's submit path, not a row action.
 */
export function useSchedulerJobMutations(): SchedulerJobMutations {
	const { t } = useTranslation();
	const { confirm } = useConfirm();

	const deleteMutation = useDeleteScheduledJob();
	const enableMutation = useSetScheduledJobEnabled();
	const triggerMutation = useTriggerScheduledJob();
	const cancelMutation = useCancelScheduledJobRun();

	const remove = useCallback(
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
					{
						onError: (error) =>
							toast.error(apiErrorMessage(error, t("pages.scheduler.errors.delete", "Could not delete the scheduled job."))),
					},
				);
			}
		},
		[confirm, deleteMutation, t],
	);

	const trigger = useCallback(
		(job: ScheduledJob) => {
			triggerMutation.mutate(
				{ path: { scheduledJobId: job.id } },
				{
					onError: (error) =>
						toast.error(apiErrorMessage(error, t("pages.scheduler.errors.trigger", "Could not trigger the job."))),
				},
			);
		},
		[triggerMutation, t],
	);

	const setEnabled = useCallback(
		(job: ScheduledJob, enabled: boolean) => {
			enableMutation.mutate(
				{ id: job.id, enabled },
				{
					onError: (error) =>
						toast.error(apiErrorMessage(error, t("pages.scheduler.errors.enable", "Could not change the job state."))),
				},
			);
		},
		[enableMutation, t],
	);

	const cancelRun = useCallback(
		(run: ScheduledJobRun) => {
			cancelMutation.mutate(
				{ path: { runId: run.id } },
				{
					onError: (error) =>
						toast.error(apiErrorMessage(error, t("pages.scheduler.errors.cancel", "Could not cancel the run."))),
				},
			);
		},
		[cancelMutation, t],
	);

	return {
		isMutating: deleteMutation.isPending || enableMutation.isPending || triggerMutation.isPending,
		isCancelling: cancelMutation.isPending,
		remove,
		trigger,
		setEnabled,
		cancelRun,
	};
}

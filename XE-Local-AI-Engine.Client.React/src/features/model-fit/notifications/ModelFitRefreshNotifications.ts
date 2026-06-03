import { toast } from "@/core/ui/notifications/Toast";
import type { ScheduledJobRun, ScheduledRunStatus } from "@/features/scheduler/models/SchedulerModels";

const refreshToastId = "model-fit-refresh";
const notifiedRunIds = new Set<string>();

export type ModelFitRefreshTerminalEventName =
	| "scheduler.runCompleted"
	| "scheduler.runFailed"
	| "scheduler.runCancelled";

type Translate = (key: string) => string;
type TerminalRefreshRunStatus = Extract<ScheduledRunStatus, "Succeeded" | "Failed" | "Cancelled" | "TimedOut" | "Skipped">;

interface ModelFitRefreshNotification {
	readonly runId?: string;
	readonly status: TerminalRefreshRunStatus;
	readonly errorMessage?: string;
}

export function isTerminalRefreshRunStatus(status: ScheduledRunStatus): status is TerminalRefreshRunStatus {
	return (
		status === "Succeeded" ||
		status === "Failed" ||
		status === "Cancelled" ||
		status === "TimedOut" ||
		status === "Skipped"
	);
}

function show(notification: ModelFitRefreshNotification, t: Translate): void {
	if (notification.runId !== undefined) {
		if (notifiedRunIds.has(notification.runId)) {
			return;
		}
		notifiedRunIds.add(notification.runId);
	}

	const toastId = notification.runId ? `${refreshToastId}-${notification.runId}` : refreshToastId;

	switch (notification.status) {
		case "Succeeded":
			toast.success(t("pages.modelFit.recommendations.toasts.success"), {
				title: t("pages.modelFit.recommendations.toasts.successTitle"),
				autoClose: 5000,
				id: toastId,
			});
			break;
		case "Cancelled":
			toast.warn(t("pages.modelFit.recommendations.toasts.cancelled"), {
				autoClose: 5000,
				id: toastId,
			});
			break;
		case "Failed":
		case "TimedOut":
		case "Skipped":
			toast.error(notification.errorMessage?.trim() || t("pages.modelFit.recommendations.toasts.failFallback"), {
				title: t("pages.modelFit.recommendations.toasts.failTitle"),
				autoClose: false,
				id: toastId,
			});
			break;
		default:
			break;
	}
}

export function notifyModelFitRefreshRun(run: ScheduledJobRun, t: Translate): void {
	if (!isTerminalRefreshRunStatus(run.status)) {
		return;
	}

	show(
		{
			runId: run.id,
			status: run.status,
			errorMessage: run.errorMessage ?? undefined,
		},
		t,
	);
}

export function notifyModelFitRefreshEvent(
	eventName: ModelFitRefreshTerminalEventName,
	fields: { readonly runId?: string; readonly errorMessage?: string },
	t: Translate,
): void {
	switch (eventName) {
		case "scheduler.runCompleted":
			show({ runId: fields.runId, status: "Succeeded" }, t);
			break;
		case "scheduler.runFailed":
			show({ runId: fields.runId, status: "Failed", errorMessage: fields.errorMessage }, t);
			break;
		case "scheduler.runCancelled":
			show({ runId: fields.runId, status: "Cancelled" }, t);
			break;
		default:
			break;
	}
}

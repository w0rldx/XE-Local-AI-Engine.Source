import { toast } from "@/core/ui/notifications/Toast";

// Generic scheduler run-completion toasts for the Scheduler management page. Unlike the model-fit hook (which renders a
// rich, template-specific toast for the one recommendation-check job), this surfaces a terminal outcome for ANY
// scheduled task so the operator always learns when a job finishes. The two never double-toast: useSchedulerHub mounts
// only on the Scheduler page and useModelFitSchedulerEvents only on the Recommendations page, so at most one is live.

const schedulerToastId = "scheduler-run";

// Run ids already toasted, so a reconnect catch-up / StrictMode double-invoke / duplicate push never double-notifies.
// Bounded so a long-lived session cannot grow it without limit; the Set's insertion order makes the oldest entry the
// first to evict.
const notifiedRunIds = new Set<string>();
const MAX_TRACKED_RUN_IDS = 200;

// The terminal run events that warrant a toast. runStarted / runProgress are intentionally excluded (too noisy — the
// page already live-refreshes its run table from the same pushes).
const TERMINAL_EVENT_NAMES = new Set(["scheduler.runCompleted", "scheduler.runFailed", "scheduler.runCancelled"]);

type Translate = (key: string) => string;

// Sanitized run payload (camelCase wire shape of SchedulerRunHubEvent). Only the safe display fields are read; the
// payload is untrusted wire data, so every field is narrowed defensively and non-strings become undefined.
function readStringField(payload: unknown, key: "runId" | "errorMessage"): string | undefined {
	if (typeof payload !== "object" || payload === null) {
		return undefined;
	}
	const value = (payload as Record<string, unknown>)[key];
	return typeof value === "string" && value.trim().length > 0 ? value : undefined;
}

// Records a run id as toasted; returns false if it was already seen (so the caller suppresses the duplicate). Evicts the
// oldest tracked id once the bound is exceeded.
function rememberRunId(runId: string): boolean {
	if (notifiedRunIds.has(runId)) {
		return false;
	}
	notifiedRunIds.add(runId);
	if (notifiedRunIds.size > MAX_TRACKED_RUN_IDS) {
		const oldest = notifiedRunIds.values().next().value;
		if (oldest !== undefined) {
			notifiedRunIds.delete(oldest);
		}
	}
	return true;
}

/**
 * Raises a terminal toast for a scheduler run event (success/cancel are localized one-liners; failure shows the
 * sanitized backend message when present, else a localized fallback). Non-terminal events and already-notified run ids
 * are no-ops. The toast id is keyed by run id so repeated pushes for the same run coalesce into one notification.
 */
export function notifySchedulerRunEvent(eventName: string, payload: unknown, t: Translate): void {
	if (!TERMINAL_EVENT_NAMES.has(eventName)) {
		return;
	}

	const runId = readStringField(payload, "runId");
	if (runId && !rememberRunId(runId)) {
		return;
	}

	const id = runId ? `${schedulerToastId}-${runId}` : schedulerToastId;

	if (eventName === "scheduler.runCompleted") {
		toast.success(t("pages.scheduler.toasts.completed"), { title: t("pages.scheduler.toasts.completedTitle"), id });
		return;
	}

	if (eventName === "scheduler.runFailed") {
		const message = readStringField(payload, "errorMessage") ?? t("pages.scheduler.toasts.failedFallback");
		toast.error(message, { title: t("pages.scheduler.toasts.failedTitle"), id });
		return;
	}

	toast.warn(t("pages.scheduler.toasts.cancelled"), { title: t("pages.scheduler.toasts.cancelledTitle"), id });
}

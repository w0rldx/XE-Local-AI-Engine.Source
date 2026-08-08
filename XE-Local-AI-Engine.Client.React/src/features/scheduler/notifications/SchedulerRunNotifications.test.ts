import { beforeEach, describe, expect, it, vi } from "vitest";

const toastMock = vi.hoisted(() => ({
	success: vi.fn(),
	error: vi.fn(),
	warn: vi.fn(),
	info: vi.fn(),
}));

vi.mock("@/core/ui/notifications/Toast", () => ({ toast: toastMock }));

import { notifySchedulerRunEvent } from "@/features/scheduler/notifications/SchedulerRunNotifications";

// Echo the key so assertions can check WHICH i18n key drove the toast without depending on translated copy.
const t = (key: string): string => key;

describe("notifySchedulerRunEvent", () => {
	beforeEach(() => {
		vi.clearAllMocks();
	});

	it("raises a success toast on a completed run", () => {
		notifySchedulerRunEvent("scheduler.runCompleted", { runId: "run-completed-1" }, t);

		expect(toastMock.success).toHaveBeenCalledWith("pages.scheduler.toasts.completed", {
			title: "pages.scheduler.toasts.completedTitle",
			id: "scheduler-run-run-completed-1",
		});
	});

	it("raises an error toast carrying the sanitized backend message on a failed run", () => {
		notifySchedulerRunEvent("scheduler.runFailed", { runId: "run-failed-1", errorMessage: "boom" }, t);

		expect(toastMock.error).toHaveBeenCalledWith("boom", {
			title: "pages.scheduler.toasts.failedTitle",
			id: "scheduler-run-run-failed-1",
		});
	});

	it("falls back to a localized message when a failed run carries no error message", () => {
		notifySchedulerRunEvent("scheduler.runFailed", { runId: "run-failed-2" }, t);

		expect(toastMock.error).toHaveBeenCalledWith("pages.scheduler.toasts.failedFallback", expect.objectContaining({
			title: "pages.scheduler.toasts.failedTitle",
		}));
	});

	it("raises a warning toast on a cancelled run", () => {
		notifySchedulerRunEvent("scheduler.runCancelled", { runId: "run-cancelled-1" }, t);

		expect(toastMock.warn).toHaveBeenCalledWith("pages.scheduler.toasts.cancelled", {
			title: "pages.scheduler.toasts.cancelledTitle",
			id: "scheduler-run-run-cancelled-1",
		});
	});

	it("ignores non-terminal run events (started / progress)", () => {
		notifySchedulerRunEvent("scheduler.runStarted", { runId: "run-started-1" }, t);
		notifySchedulerRunEvent("scheduler.runProgress", { runId: "run-progress-1" }, t);

		expect(toastMock.success).not.toHaveBeenCalled();
		expect(toastMock.error).not.toHaveBeenCalled();
		expect(toastMock.warn).not.toHaveBeenCalled();
	});

	it("toasts a given run id only once across repeated terminal pushes (dedupe)", () => {
		notifySchedulerRunEvent("scheduler.runCompleted", { runId: "run-dedupe-1" }, t);
		notifySchedulerRunEvent("scheduler.runCompleted", { runId: "run-dedupe-1" }, t);

		expect(toastMock.success).toHaveBeenCalledTimes(1);
	});

	it("still toasts when no run id is present (cannot dedupe, but must not be silent)", () => {
		notifySchedulerRunEvent("scheduler.runCompleted", {}, t);

		expect(toastMock.success).toHaveBeenCalledWith("pages.scheduler.toasts.completed", {
			title: "pages.scheduler.toasts.completedTitle",
			id: "scheduler-run",
		});
	});
});

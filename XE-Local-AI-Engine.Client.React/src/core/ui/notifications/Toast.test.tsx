// @vitest-environment jsdom

import { afterEach, describe, expect, it, vi } from "vitest";

// Mock Mantine at the call boundary so the open-or-update lifecycle is asserted without a real DOM/provider.
const notificationsMock = vi.hoisted(() => ({ show: vi.fn(), update: vi.fn() }));
vi.mock("@mantine/notifications", () => ({ notifications: notificationsMock }));
// No test i18n instance; resolve inline defaults.
vi.mock("i18next", () => ({ t: (key: string, fallback?: string) => fallback ?? key }));

import { toast } from "@/core/ui/notifications/Toast";

describe("toast", () => {
	afterEach(() => {
		vi.clearAllMocks();
	});

	it("progress opens a sticky loading toast (loading, no auto-close, no close button) via show+update", () => {
		toast.progress({ id: "pull-x", title: "Pulling x", message: "downloading", percent: 42 });

		const expected = {
			id: "pull-x",
			title: "Pulling x",
			message: "downloading — 42%",
			loading: true,
			autoClose: false,
			withCloseButton: false,
		};
		expect(notificationsMock.show).toHaveBeenCalledWith(expect.objectContaining(expected));
		expect(notificationsMock.update).toHaveBeenCalledWith(expect.objectContaining(expected));
	});

	// Regression: finalizing with the SAME id used by a sticky progress toast must call notifications.update so the
	// loading spinner is cleared, the close button restored, and the auto-close timer re-armed — otherwise the toast
	// stayed stuck on screen showing the last progress status ("success"), dismissable only by manual swipe.
	it("success finalizes an existing progress toast by clearing loading, restoring close button, and re-arming auto-close", () => {
		toast.success("x is ready.", { id: "pull-x", title: "Model pulled" });

		const finalize = { id: "pull-x", title: "Model pulled", message: "x is ready.", loading: false, withCloseButton: true };
		expect(notificationsMock.update).toHaveBeenCalledWith(expect.objectContaining(finalize));
		expect(notificationsMock.update.mock.calls[0]?.[0]?.autoClose).toBe(5000);
	});

	// Operator policy: an error stays until dismissed, so a progress→error finalize must keep the timer off while still
	// clearing the spinner and restoring the close button (the only way to dismiss it).
	it("error finalize is a sticky, closable, non-loading update keyed to the same id", () => {
		toast.error("Could not pull x.", { id: "pull-x", title: "Pull failed" });

		const updatePayload = notificationsMock.update.mock.calls[0]?.[0];
		expect(updatePayload).toMatchObject({ id: "pull-x", loading: false, withCloseButton: true, autoClose: false });
	});

	it.each([
		["success", 5000, () => toast.success("s")],
		["info", 5000, () => toast.info("i")],
		["warning", 15_000, () => toast.warning("w")],
		["warn", 15_000, () => toast.warn("w")],
		["error", false, () => toast.error("e")],
	] as const)("defaults %s to autoClose %s and keeps the close button", (_type, expected, fire) => {
		fire();

		expect(notificationsMock.show.mock.calls[0]?.[0]).toMatchObject({ autoClose: expected, withCloseButton: true });
		expect(notificationsMock.update.mock.calls[0]?.[0]).toMatchObject({ autoClose: expected, withCloseButton: true });
	});

	it("an explicit autoClose option is honored over the default", () => {
		toast.info("hi", { id: "info-1", autoClose: 1234 });
		toast.error("bad", { id: "error-1", autoClose: 8000 });
		toast.warning("careful", { id: "warning-1", autoClose: false });

		expect(notificationsMock.update.mock.calls.map((call) => call[0]?.autoClose)).toEqual([1234, 8000, false]);
	});

	// Regression guard: callers derive a toast message from a server failure, and ApiError deliberately resolves its
	// message to "" when the response carried no detail, message or title. Passed straight through, Mantine renders a
	// colored, iconed, dismissable notification containing no text at all — a signal that says nothing.
	it("substitutes a generic message rather than firing an empty error toast", () => {
		toast.error("");

		expect(notificationsMock.show.mock.calls[0]?.[0]?.message).toBe("An error occurred");
	});

	it("treats a whitespace-only message as empty", () => {
		toast.warn("   ");

		expect(notificationsMock.show.mock.calls[0]?.[0]?.message).toBe("An error occurred");
	});

	it("uses the neutral substitute for success and info, which an error sentence would misdescribe", () => {
		toast.success("");
		toast.info("");

		expect(notificationsMock.show.mock.calls[0]?.[0]?.message).toBe("Done.");
		expect(notificationsMock.show.mock.calls[1]?.[0]?.message).toBe("Done.");
	});

	// A titled toast already says something, so an appended generic body would be noise rather than a rescue.
	it("leaves an empty message alone when a title carries the text", () => {
		toast.error("", { title: "Pull failed" });

		expect(notificationsMock.show.mock.calls[0]?.[0]?.message).toBe("");
	});

	it("does not touch a message that has content", () => {
		toast.error("Could not pull x.");

		expect(notificationsMock.show.mock.calls[0]?.[0]?.message).toBe("Could not pull x.");
	});
});

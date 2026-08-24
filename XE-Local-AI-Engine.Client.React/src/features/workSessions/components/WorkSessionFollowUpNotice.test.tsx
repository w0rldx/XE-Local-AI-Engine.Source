// @vitest-environment jsdom

// The composer copy contract, asserted VERBATIM. The three promises are easy to swap and the wrong one is a lie
// about what the backend will do with the message: `Draft` has no next step until Start, `Paused`/`Interrupted`
// auto-resume on post, and `WaitingFor*` behave like `Running` because a step is already live and holding the
// node's single invocation slot.

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it } from "vitest";

import { WorkSessionFollowUpNotice } from "@/features/workSessions/components/WorkSessionFollowUpNotice";
import type { WorkSessionStatus } from "@/features/workSessions/models/WorkSessionModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

const cases: ReadonlyArray<{ status: WorkSessionStatus; notice: string }> = [
	{ status: "Draft", notice: "Saved — it will be used when you start this session." },
	{ status: "Running", notice: "Queued for the next step." },
	{ status: "WaitingForApproval", notice: "Queued for the next step." },
	{ status: "WaitingForInput", notice: "Queued for the next step." },
	{ status: "Paused", notice: "Sent — resuming." },
	{ status: "Interrupted", notice: "Sent — resuming." },
	{ status: "Completed", notice: "This session is finished and takes no further messages." },
	{ status: "Failed", notice: "This session is finished and takes no further messages." },
	{ status: "Cancelled", notice: "This session is finished and takes no further messages." },
];

describe("WorkSessionFollowUpNotice", () => {
	afterEach(() => {
		cleanup();
	});

	it.each(cases)("promises exactly the right thing in $status", ({ status, notice }) => {
		renderWithProviders(<WorkSessionFollowUpNotice status={status} />);

		expect(screen.getByTestId("work-session-follow-up-notice").textContent).toBe(notice);
	});

	it("replaces the promise with the failure when a post is rejected", () => {
		renderWithProviders(<WorkSessionFollowUpNotice status="Running" error="Message exceeds the node's size limit." />);

		expect(screen.getByTestId("work-session-follow-up-error").textContent).toContain("size limit");
		expect(screen.queryByTestId("work-session-follow-up-notice")).toBeNull();
	});
});

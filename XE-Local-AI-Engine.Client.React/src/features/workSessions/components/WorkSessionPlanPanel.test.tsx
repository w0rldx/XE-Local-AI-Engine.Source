// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { WorkSessionPlanPanel, type WorkSessionPlanPanelProps } from "@/features/workSessions/components/WorkSessionPlanPanel";
import type { WorkSessionStatus, WorkSessionTaskResponse } from "@/features/workSessions/models/WorkSessionModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

const handlers = {
	onStart: vi.fn(),
	onPause: vi.fn(),
	onResume: vi.fn(),
	onCancel: vi.fn(),
};

function task(id: string, overrides: Partial<WorkSessionTaskResponse> = {}): WorkSessionTaskResponse {
	return {
		id,
		parentTaskId: null,
		sequence: 1,
		title: `task ${id}`,
		detail: null,
		status: "Planned",
		blockedReason: null,
		origin: "Agent",
		createdStep: 1,
		updatedStep: 1,
		...overrides,
	};
}

function render(overrides: Partial<WorkSessionPlanPanelProps> = {}) {
	const props: WorkSessionPlanPanelProps = {
		status: "Running",
		stepCount: 3,
		maxStepsPerRun: 25,
		tasks: [],
		isLoadingTasks: false,
		liveUpdatesUnavailable: false,
		isCommandPending: false,
		...handlers,
		...overrides,
	};
	renderWithProviders(<WorkSessionPlanPanel {...props} />);
}

describe("WorkSessionPlanPanel", () => {
	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("nests tasks by parent and orders each level by sequence", () => {
		render({
			currentTaskId: "child-a",
			tasks: [
				task("child-b", { parentTaskId: "root", sequence: 3, title: "second child" }),
				task("child-a", { parentTaskId: "root", sequence: 2, title: "first child" }),
				task("root", { sequence: 1, title: "the root task" }),
			],
		});

		const rendered = screen.getByTestId("work-session-task-tree").textContent ?? "";
		expect(rendered.indexOf("the root task")).toBeLessThan(rendered.indexOf("first child"));
		expect(rendered.indexOf("first child")).toBeLessThan(rendered.indexOf("second child"));
		expect(screen.getByTestId("work-session-task-child-a").getAttribute("data-current")).toBe("true");
		expect(screen.getByTestId("work-session-task-child-b").getAttribute("data-current")).toBeNull();
	});

	it("shows the blocked reason on a blocked task", () => {
		render({ tasks: [task("t1", { status: "Blocked", blockedReason: "needs the operator's API key" })] });

		expect(screen.getByTestId("work-session-task-blocked-t1").textContent).toContain("needs the operator's API key");
	});

	it("renders the step counter against the effective per-run maximum", () => {
		render({ stepCount: 7, maxStepsPerRun: 25 });

		expect(screen.getByTestId("work-session-step-counter").textContent).toBe("Step 7 of 25");
	});

	const controlCases: ReadonlyArray<{ status: WorkSessionStatus; shown: readonly string[]; hidden: readonly string[] }> = [
		{ status: "Draft", shown: ["work-session-start"], hidden: ["work-session-pause", "work-session-resume", "work-session-cancel"] },
		{ status: "Running", shown: ["work-session-pause", "work-session-cancel"], hidden: ["work-session-start", "work-session-resume"] },
		{ status: "WaitingForApproval", shown: ["work-session-pause", "work-session-cancel"], hidden: ["work-session-start", "work-session-resume"] },
		{ status: "WaitingForInput", shown: ["work-session-pause", "work-session-cancel"], hidden: ["work-session-start", "work-session-resume"] },
		{ status: "Paused", shown: ["work-session-resume", "work-session-cancel"], hidden: ["work-session-start", "work-session-pause"] },
		{ status: "Interrupted", shown: ["work-session-resume"], hidden: ["work-session-start", "work-session-pause"] },
		{ status: "Completed", shown: [], hidden: ["work-session-start", "work-session-pause", "work-session-resume", "work-session-cancel"] },
		{ status: "Failed", shown: [], hidden: ["work-session-start", "work-session-pause", "work-session-resume", "work-session-cancel"] },
		{ status: "Cancelled", shown: [], hidden: ["work-session-start", "work-session-pause", "work-session-resume", "work-session-cancel"] },
	];

	it.each(controlCases)("offers only the controls that apply in $status", ({ status, shown, hidden }) => {
		render({ status });

		for (const testId of shown) {
			expect(screen.getByTestId(testId), `${status} should offer ${testId}`).toBeDefined();
		}
		for (const testId of hidden) {
			expect(screen.queryByTestId(testId), `${status} should not offer ${testId}`).toBeNull();
		}
	});

	it("wires each control to its own command and disables them while one is in flight", () => {
		render({ status: "Running" });
		fireEvent.click(screen.getByTestId("work-session-pause"));
		expect(handlers.onPause).toHaveBeenCalledTimes(1);
		expect(handlers.onCancel).not.toHaveBeenCalled();

		cleanup();
		render({ status: "Paused", isCommandPending: true });
		expect((screen.getByTestId("work-session-resume") as HTMLButtonElement).disabled).toBe(true);
		expect((screen.getByTestId("work-session-cancel") as HTMLButtonElement).disabled).toBe(true);
	});

	it("explains an interrupted session and points at its checkpoint", () => {
		render({ status: "Interrupted", latestCheckpointStep: 4 });

		expect(screen.getByTestId("work-session-interrupted-alert")).toBeDefined();
		expect(screen.getByTestId("work-session-checkpoint-hint").textContent).toContain("4");
	});

	it("shows a polling chip instead of an error when live updates are unavailable", () => {
		render({ liveUpdatesUnavailable: true });

		expect(screen.getByTestId("work-session-live-unavailable")).toBeDefined();
		expect(screen.queryByTestId("work-session-interrupted-alert")).toBeNull();
		expect(screen.queryByTestId("work-session-failed-alert")).toBeNull();
	});

	it("tells a draft session that the agent writes the plan itself", () => {
		render({ status: "Draft" });

		expect(screen.getByTestId("work-session-task-tree-empty").textContent).toContain("draft the plan");
	});
});

// An unanswered approval is cancelled by the supervisor after MaxParkedSeconds: it checkpoints and lands Paused,
// which is the DESIGNED mitigation for a parked step holding the node's single invocation slot — so the panel must
// render it as an ordinary paused session, with no error styling anywhere.
describe("WorkSessionPlanPanel after an unanswered approval times out", () => {
	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders the resulting Paused session as ordinary, not as a failure", () => {
		renderWithProviders(
			<WorkSessionPlanPanel
				status="Paused"
				stepCount={5}
				maxStepsPerRun={25}
				tasks={[]}
				isLoadingTasks={false}
				latestCheckpointStep={5}
				liveUpdatesUnavailable={false}
				isCommandPending={false}
				{...handlers}
			/>,
		);

		expect(screen.getByTestId("work-session-status-badge").textContent).toBe("Paused");
		expect(screen.queryByTestId("work-session-failed-alert")).toBeNull();
		expect(screen.queryByTestId("work-session-interrupted-alert")).toBeNull();
		expect(screen.queryByTestId("work-session-waiting-approval-hint")).toBeNull();
		// The recovery path is the ordinary one.
		expect(screen.getByTestId("work-session-resume")).toBeDefined();
		expect(screen.getByTestId("work-session-checkpoint-hint").textContent).toContain("5");
	});
});

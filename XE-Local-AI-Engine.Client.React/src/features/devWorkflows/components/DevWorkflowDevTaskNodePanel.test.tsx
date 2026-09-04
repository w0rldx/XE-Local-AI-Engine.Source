// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { DevWorkflowDevTaskNodePanel } from "@/features/devWorkflows/components/DevWorkflowDevTaskNodePanel";
import { devWorkflowNodeRunDetail } from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

const navigate = vi.hoisted(() => vi.fn());

vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigate,
}));

const projectId = "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";
const taskId = "bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";

describe("DevWorkflowDevTaskNodePanel", () => {
	beforeEach(() => {
		navigate.mockClear();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders the task's stage and deep-links Dev Mode to the project and task this node drove", () => {
		renderWithProviders(
			<DevWorkflowDevTaskNodePanel
				nodeRun={devWorkflowNodeRunDetail({
					nodeType: "DevTask",
					developmentProjectId: projectId,
					developmentTaskId: taskId,
					outputJson: JSON.stringify({ status: "Succeeded", attempt: 1, taskStatus: "AwaitingApply", reviewRound: 2 }),
				})}
			/>,
		);

		expect(screen.getByTestId("dev-workflow-node-devtask-stage").textContent).toBe("Awaiting apply");
		expect(screen.getByTestId("dev-workflow-node-devtask-round").textContent).toBe("round 2");

		fireEvent.click(screen.getByTestId("dev-workflow-node-development-link"));

		// X8: the pointer is the workflow's whole contribution to the Dev Mode surface, so it has to arrive complete.
		expect(navigate).toHaveBeenCalledWith({ to: "/development", search: { project: projectId, task: taskId } });
	});

	it("says the stage is unreported rather than printing the previous attempt's on a re-attempt", () => {
		// The store's Pending re-attempt branch does NOT clear OutputJson, so attempt 1's document is still on the row
		// while attempt 2 is implementing. "Awaiting apply" here would be a stage nothing is in.
		renderWithProviders(
			<DevWorkflowDevTaskNodePanel
				nodeRun={devWorkflowNodeRunDetail({
					nodeType: "DevTask",
					status: "Running",
					attempt: 2,
					developmentProjectId: projectId,
					developmentTaskId: taskId,
					outputJson: JSON.stringify({ status: "Failed", attempt: 1, taskStatus: "AwaitingApply", reviewRound: 1 }),
				})}
			/>,
		);

		expect(screen.getByTestId("dev-workflow-node-devtask-nostage")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-node-devtask-stage")).toBeNull();
	});

	it("prints a task status a newer server invented rather than mislabelling it as a known one", () => {
		renderWithProviders(
			<DevWorkflowDevTaskNodePanel
				nodeRun={devWorkflowNodeRunDetail({
					nodeType: "DevTask",
					developmentProjectId: projectId,
					developmentTaskId: taskId,
					outputJson: JSON.stringify({ attempt: 1, taskStatus: "AwaitingSignoff" }),
				})}
			/>,
		);

		expect(screen.getByTestId("dev-workflow-node-devtask-stage").textContent).toBe("AwaitingSignoff");
	});

	it("links out plainly when the node names no project, because there is nothing to seed", () => {
		renderWithProviders(
			<DevWorkflowDevTaskNodePanel
				nodeRun={devWorkflowNodeRunDetail({ nodeType: "DevTask", developmentProjectId: null, developmentTaskId: taskId })}
			/>,
		);

		fireEvent.click(screen.getByTestId("dev-workflow-node-development-link"));

		expect(navigate).toHaveBeenCalledWith({ to: "/development" });
	});
});

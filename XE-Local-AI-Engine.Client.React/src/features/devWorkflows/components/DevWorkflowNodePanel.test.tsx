// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { DevWorkflowNodePanel } from "@/features/devWorkflows/components/DevWorkflowNodePanel";
import type { DevWorkflowNodeRunDetailResponse } from "@/features/devWorkflows/models/DevWorkflowModels";
import { devWorkflowNodeRunDetail } from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

const navigate = vi.hoisted(() => vi.fn());

vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigate,
}));

const sessionId = "88888888-8888-4888-8888-888888888888";

function renderPanel(nodeRun: DevWorkflowNodeRunDetailResponse, onShowArtifacts = vi.fn()) {
	renderWithProviders(
		<ConfirmProvider>
			<DevWorkflowNodePanel
				nodeRun={nodeRun}
				isPending={false}
				isDeciding={false}
				onDecide={vi.fn()}
				onShowArtifacts={onShowArtifacts}
				onClose={vi.fn()}
			/>
		</ConfirmProvider>,
	);
	return { onShowArtifacts };
}

describe("DevWorkflowNodePanel", () => {
	beforeEach(() => {
		navigate.mockClear();
	});

	afterEach(() => {
		cleanup();
	});

	it("links an agent node out to its work session rather than re-hosting the session view", () => {
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Agent", workSessionId: sessionId, workSessionAvailable: true }));

		fireEvent.click(screen.getByTestId("dev-workflow-node-session-link"));

		expect(navigate).toHaveBeenCalledWith({ to: "/work-sessions/$sessionId", params: { sessionId } });
	});

	it("says the transcript is gone — not that the node is empty — when the work session was purged", () => {
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Agent", workSessionId: sessionId, workSessionAvailable: false }));

		// The node-run row outlives its session on purpose (the reference is loose), so the UI must name WHICH thing is
		// missing: the workflow-owned events and artifacts are still there.
		expect(screen.getByTestId("dev-workflow-node-session-purged")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-node-session-link")).toBeNull();
	});

	it("reports how many times a node survived a restart, which is the module's whole claim", () => {
		renderPanel(devWorkflowNodeRunDetail({ sessionResumes: 2 }));

		expect(screen.getByTestId("dev-workflow-node-resumes").textContent).toBe("resumed 2×");
	});

	it("explains a failed node with its failure class and sanitized reason", () => {
		renderPanel(
			devWorkflowNodeRunDetail({ status: "Failed", failureClass: "Timeout", terminalReason: "no step in 900s", completedAtUtc: 2 }),
		);

		const failure = screen.getByTestId("dev-workflow-node-failure");
		expect(failure.textContent).toContain("Timed out");
		expect(failure.textContent).toContain("no step in 900s");
	});

	it("falls back to a plain sentence for a failure class a newer server invented", () => {
		renderPanel(devWorkflowNodeRunDetail({ status: "Failed", failureClass: "QuantumFluctuation" }));

		expect(screen.getByTestId("dev-workflow-node-failure").textContent).toContain("The node failed");
		expect(screen.getByTestId("dev-workflow-node-failure").textContent).not.toContain("QuantumFluctuation");
	});

	it("sends a tool node's validation report to the artifacts tab instead of re-rendering it here", () => {
		const { onShowArtifacts } = renderPanel(
			devWorkflowNodeRunDetail({ nodeType: "Tool", primaryArtifactId: "99999999-9999-4999-8999-999999999999" }),
		);

		fireEvent.click(screen.getByTestId("dev-workflow-node-tool-report"));

		expect(onShowArtifacts).toHaveBeenCalledTimes(1);
	});

	it("shows a DevTask node's task id and links to Dev Mode, which owns the evidence chain", () => {
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "DevTask", developmentTaskId: "task-7" }));

		expect(screen.getByTestId("dev-workflow-node-devtask-id").textContent).toBe("task-7");
		fireEvent.click(screen.getByTestId("dev-workflow-node-development-link"));
		expect(navigate).toHaveBeenCalledWith({ to: "/development" });
	});

	it("renders a structural node with no link-outs at all", () => {
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Join", label: "Join", workSessionId: null }));

		expect(screen.getByTestId("dev-workflow-node-panel-label").textContent).toBe("Join");
		expect(screen.queryByTestId("dev-workflow-node-agent")).toBeNull();
		expect(screen.queryByTestId("dev-workflow-node-tool")).toBeNull();
		expect(screen.queryByTestId("dev-workflow-node-devtask")).toBeNull();
	});

	it("renders inputJson as raw text, because nothing parses it in v1", () => {
		renderPanel(devWorkflowNodeRunDetail({ inputJson: '{"workItemRequest":"Compare the options"}' }));

		expect(screen.getByTestId("dev-workflow-node-input").textContent).toBe('{"workItemRequest":"Compare the options"}');
	});
});

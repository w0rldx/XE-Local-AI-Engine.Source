// @vitest-environment jsdom

import { fireEvent, screen } from "@testing-library/react";
import type { ReactNode } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { toWorkflowPath } from "@/features/chat/workflow/ChatWorkflowModels";
import { WorkflowRunStatusCard } from "@/features/chat/workflow/WorkflowRunStatusCard";
import { chatGraph, eightNodeGraph, makeNodeRun } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The card links to the run view; the router itself is not this file's subject, the search it carries is.
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	Link: ({ children, to, search, ...props }: { children: ReactNode; to: string; search: Record<string, string> }) => (
		<a href={`${to}?${new URLSearchParams(search).toString()}`} {...props}>
			{children}
		</a>
	),
}));

const runningPath = toWorkflowPath(eightNodeGraph, [
	makeNodeRun({ nodeKey: "start", status: "Succeeded" }),
	makeNodeRun({ nodeKey: "analyze", status: "Succeeded" }),
	makeNodeRun({ nodeKey: "check", status: "Succeeded" }),
	makeNodeRun({ nodeKey: "review", status: "Skipped", startedAtUtc: null }),
	makeNodeRun({ nodeKey: "lookup", status: "Running", startedAtUtc: 1_700_000_200_000, completedAtUtc: null }),
]);

function renderCard(status: string, overrides: Partial<Parameters<typeof WorkflowRunStatusCard>[0]> = {}) {
	const props = {
		runId: "run-1",
		definitionId: "def-1",
		workflowName: "Support triage",
		status,
		path: runningPath,
		stopping: false,
		onStop: vi.fn(),
		onDismiss: vi.fn(),
		...overrides,
	};
	renderWithProviders(<WorkflowRunStatusCard {...props} />);
	return props;
}

describe("WorkflowRunStatusCard", () => {
	afterEach(() => {
		vi.useRealTimers();
	});

	it("shows the taken path, the active node with its elapsed time, and Stops the run", () => {
		vi.useFakeTimers({ now: 1_700_000_242_000, toFake: ["Date"] });
		const props = renderCard("Running");

		expect(screen.getByTestId("chat-workflow-status-name").textContent).toBe("Support triage");
		expect(screen.getByTestId("chat-workflow-status-node-analyze").textContent).toBe("✓ Analyze");
		expect(screen.getByTestId("chat-workflow-status-node-lookup").textContent).toBe("● Read file");
		expect(screen.getByTestId("chat-workflow-status-node-done").textContent).toBe("○ Done");
		expect(screen.getByTestId("chat-workflow-status-node-review").getAttribute("data-status")).toBe("Skipped");
		expect(screen.getByTestId("chat-workflow-status-active").textContent).toBe("Read file · 42s");
		expect(screen.getByTestId("chat-workflow-status-view").getAttribute("href")).toBe(
			"/graph-workflows?runId=run-1&definitionId=def-1",
		);
		expect(screen.queryByTestId("chat-workflow-status-dismiss")).toBeNull();

		fireEvent.click(screen.getByTestId("chat-workflow-status-stop"));
		expect(props.onStop).toHaveBeenCalledTimes(1);
	});

	it("names the model an active model-calling node runs on, reading 'default model' when the config names none", () => {
		vi.useFakeTimers({ now: 1_700_000_210_000, toFake: ["Date"] });
		const withModel = structuredClone(eightNodeGraph);
		const agentPath = (graph: typeof eightNodeGraph) =>
			toWorkflowPath(graph, [makeNodeRun({ nodeKey: "analyze", status: "Running", completedAtUtc: null })]);

		const { unmount } = renderWithProviders(
			<WorkflowRunStatusCard
				runId="run-1"
				definitionId="def-1"
				workflowName="Support triage"
				status="Running"
				path={agentPath(eightNodeGraph)}
				stopping={false}
				onStop={vi.fn()}
				onDismiss={vi.fn()}
			/>,
		);
		expect(screen.getByTestId("chat-workflow-status-active").textContent).toBe("Analyze · default model · 10s");
		unmount();

		const analyze = withModel.nodes?.find((node) => node.key === "analyze");
		(analyze?.config as { model: string | null }).model = "qwen3-8b";
		renderCard("Running", { path: agentPath(withModel) });
		expect(screen.getByTestId("chat-workflow-status-active").textContent).toBe("Analyze · qwen3-8b · 10s");
	});

	it("says where a failed run stopped and why, and can be dismissed", () => {
		const failedPath = toWorkflowPath(eightNodeGraph, [makeNodeRun({ nodeKey: "lookup", status: "Failed" })]);
		const props = renderCard("Failed", { path: failedPath, failureReason: "The file is missing." });

		expect(screen.getByTestId("chat-workflow-status-terminal").textContent).toBe("Failed at Read file: The file is missing.");
		expect(screen.queryByTestId("chat-workflow-status-stop")).toBeNull();

		fireEvent.click(screen.getByTestId("chat-workflow-status-dismiss"));
		expect(props.onDismiss).toHaveBeenCalledTimes(1);
	});

	it("names the node a cancelled run stopped at, and can be dismissed like every other terminal state", () => {
		const cancelledPath = toWorkflowPath(eightNodeGraph, [makeNodeRun({ nodeKey: "analyze", status: "Cancelled" })]);
		const props = renderCard("Cancelled", { path: cancelledPath });
		expect(screen.getByTestId("chat-workflow-status-terminal").textContent).toBe("Cancelled during Analyze");
		expect(screen.queryByTestId("chat-workflow-status-stop")).toBeNull();

		fireEvent.click(screen.getByTestId("chat-workflow-status-dismiss"));
		expect(props.onDismiss).toHaveBeenCalledTimes(1);
	});

	it("says a run parked on a ChatInput waits for your input, and one parked on a Pause for a decision", () => {
		const askPath = toWorkflowPath(chatGraph, [
			makeNodeRun({ nodeKey: "ask", kind: "ChatInput", status: "WaitingForApproval", pendingDecisionKind: "Answer" }),
		]);
		const { unmount } = renderWithProviders(
			<WorkflowRunStatusCard
				runId="run-1"
				definitionId="def-1"
				workflowName="Support triage"
				status="WaitingForApproval"
				path={askPath}
				stopping={false}
				onStop={vi.fn()}
				onDismiss={vi.fn()}
			/>,
		);
		expect(screen.getByTestId("chat-workflow-status-badge").textContent).toBe("Waiting for your input");
		unmount();

		const pausePath = toWorkflowPath(eightNodeGraph, [
			makeNodeRun({ nodeKey: "review", kind: "Pause", status: "WaitingForApproval", pendingDecisionKind: "Approve" }),
		]);
		renderCard("WaitingForApproval", { path: pausePath });
		expect(screen.getByTestId("chat-workflow-status-badge").textContent).toBe("Waiting for a decision");
	});

	it("reads Completed for a finished run", () => {
		renderCard("Completed");
		expect(screen.getByTestId("chat-workflow-status-terminal").textContent).toBe("Completed");
		expect(screen.queryByTestId("chat-workflow-status-active")).toBeNull();
	});

	it("does not offer Stop twice while the run is already cancelling", () => {
		renderCard("Cancelling");
		expect(screen.getByTestId("chat-workflow-status-stop")).toHaveProperty("disabled", true);
	});
});

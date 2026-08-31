// @vitest-environment jsdom

import { cleanup, fireEvent, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { DevWorkflowNodePanel } from "@/features/devWorkflows/components/DevWorkflowNodePanel";
import type {
	DevWorkflowNodeRunDetailResponse,
	DevWorkflowRunEventResponse,
	DevWorkflowRunResponse,
} from "@/features/devWorkflows/models/DevWorkflowModels";
import {
	devWorkflowNodeRunDetail,
	devWorkflowNodeRunSummary,
	devWorkflowRun,
	devWorkflowRunEvent,
} from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

const navigate = vi.hoisted(() => vi.fn());

vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigate,
}));

const sessionId = "88888888-8888-4888-8888-888888888888";

interface PanelOptions {
	readonly onShowArtifacts?: () => void;
	/** The run's loaded event pages, exactly as the detail page hands them over — unfiltered, every node's rows. */
	readonly events?: readonly DevWorkflowRunEventResponse[];
	readonly run?: DevWorkflowRunResponse;
}

function renderPanel(nodeRun: DevWorkflowNodeRunDetailResponse, options: PanelOptions = {}) {
	const onShowArtifacts = options.onShowArtifacts ?? vi.fn();
	renderWithProviders(
		<ConfirmProvider>
			<DevWorkflowNodePanel
				nodeRun={nodeRun}
				isPending={false}
				isDeciding={false}
				events={options.events}
				run={options.run}
				onDecide={vi.fn()}
				onShowArtifacts={onShowArtifacts}
				onClose={vi.fn()}
			/>
		</ConfirmProvider>,
	);
	return { onShowArtifacts };
}

/** `node.interrupted` rows for the fixture node, which is what the panel counts restarts from. */
function interruptedEvents(count: number): readonly DevWorkflowRunEventResponse[] {
	return Array.from({ length: count }, (_, index) =>
		devWorkflowRunEvent({
			id: `interrupted-${index}`,
			sequence: index + 1,
			eventType: "node.interrupted",
			nodeRunId: devWorkflowNodeRunDetail().id,
		}),
	);
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

	it("reports restart survival from the event log, which is the module's whole claim", () => {
		renderPanel(devWorkflowNodeRunDetail({ sessionResumes: 0 }), { events: interruptedEvents(1) });

		expect(screen.getByTestId("dev-workflow-node-interrupted").textContent).toBe("interrupted and re-dispatched 1×");
	});

	it("does not pass step-budget parking off as a restart — they are different facts, shown separately", () => {
		// Live proof of the bug this replaces: a node that had NEVER been interrupted parked 4× and the pane claimed
		// "resumed 4×", while the node that actually survived a restart showed nothing at all.
		renderPanel(devWorkflowNodeRunDetail({ sessionResumes: 4 }), { events: interruptedEvents(0) });

		expect(screen.getByTestId("dev-workflow-node-resumes").textContent).toBe("paused for step budget 4×");
		expect(screen.queryByTestId("dev-workflow-node-interrupted")).toBeNull();
	});

	it("counts only THIS node's interruptions, out of a feed that carries every node's", () => {
		// The panel is handed the whole run feed now. A sibling's restart is not this node's evidence.
		renderPanel(devWorkflowNodeRunDetail(), {
			events: [
				...interruptedEvents(1),
				devWorkflowRunEvent({ id: "other", sequence: 9, eventType: "node.interrupted", nodeRunId: "some-other-node" }),
			],
		});

		expect(screen.getByTestId("dev-workflow-node-interrupted").textContent).toBe("interrupted and re-dispatched 1×");
	});

	it("lists prior attempts from the event log, which is the only place they exist", () => {
		const nodeRunId = devWorkflowNodeRunDetail().id;
		renderPanel(devWorkflowNodeRunDetail({ attempt: 2, maxAttempts: 3 }), {
			events: [
				devWorkflowRunEvent({
					id: "attached-1",
					sequence: 1,
					eventType: "worksession.attached",
					nodeRunId,
					detailJson: JSON.stringify({ workSessionId: sessionId, attempt: 1, sessionResumes: 0 }),
				}),
				devWorkflowRunEvent({
					id: "retry-1",
					sequence: 2,
					eventType: "node.retry.scheduled",
					nodeRunId,
					outcome: "provider-error",
				}),
			],
		});

		expect(screen.getByTestId("dev-workflow-node-attempt-1").textContent).toContain("provider-error");
		// Attempt 2 is the one running: the row exists because the node-run says so, with nothing claimed about it yet.
		expect(screen.getByTestId("dev-workflow-node-attempt-2")).toBeDefined();
		fireEvent.click(screen.getByTestId("dev-workflow-node-attempt-session-1"));
		expect(navigate).toHaveBeenCalledWith({ to: "/work-sessions/$sessionId", params: { sessionId } });
	});

	it("says why a completed node is running again, rather than letting it silently un-complete", () => {
		const nodeRunId = devWorkflowNodeRunDetail().id;
		renderPanel(devWorkflowNodeRunDetail({ nodeKey: "implement", attempt: 2 }), {
			run: devWorkflowRun({
				nodes: [devWorkflowNodeRunSummary({ id: "node-validate", nodeKey: "validate", label: "Validate the patch" })],
			}),
			events: [
				devWorkflowRunEvent({
					id: "routed",
					sequence: 4,
					eventType: "node.retry.routed",
					nodeRunId: "node-validate",
					detailJson: JSON.stringify({ nodeKey: "validate", retryTarget: "implement" }),
				}),
				devWorkflowRunEvent({ id: "reset", sequence: 5, eventType: "node.retry.scheduled", nodeRunId }),
			],
		});

		expect(screen.getByTestId("dev-workflow-node-cascade-rerun").textContent).toContain("Validate the patch");
	});

	it("does not call a node's own retry a cascade — that is just this node trying again", () => {
		const nodeRunId = devWorkflowNodeRunDetail().id;
		renderPanel(devWorkflowNodeRunDetail({ nodeKey: "implement", attempt: 2 }), {
			events: [
				devWorkflowRunEvent({
					id: "routed",
					sequence: 4,
					eventType: "node.retry.routed",
					nodeRunId,
					detailJson: JSON.stringify({ nodeKey: "implement", retryTarget: "implement" }),
				}),
				devWorkflowRunEvent({ id: "reset", sequence: 5, eventType: "node.retry.scheduled", nodeRunId }),
			],
		});

		expect(screen.queryByTestId("dev-workflow-node-cascade-rerun")).toBeNull();
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

	it("shows a Join's dependencies split into satisfied and outstanding, from the runtime's own answer", () => {
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Join", nodeKey: "integrate", label: "Integrate" }), {
			run: devWorkflowRun({
				graph: {
					schemaVersion: 1,
					nodes: [],
					edges: [
						{ from: "implement#0", to: "integrate" },
						{ from: "implement#1", to: "integrate" },
					],
				},
				nodes: [
					devWorkflowNodeRunSummary({ id: "n0", nodeKey: "integrate", waitingOnNodeKeys: ["implement#1"] }),
					devWorkflowNodeRunSummary({ id: "n1", nodeKey: "implement#0", label: "Slice one", status: "Succeeded" }),
					devWorkflowNodeRunSummary({ id: "n2", nodeKey: "implement#1", label: "Slice two", status: "Running" }),
				],
			}),
		});

		expect(screen.getByTestId("dev-workflow-node-dependency-implement#0").textContent).toContain("satisfied");
		expect(screen.getByTestId("dev-workflow-node-dependency-implement#1").textContent).toContain("outstanding");
		expect(screen.getByTestId("dev-workflow-node-dependency-implement#0").textContent).toContain("Slice one");
	});

	it("shows a Gate's branches with their stored conditions, and marks the one the run actually took", () => {
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Gate", nodeKey: "verdict", label: "Verdict" }), {
			run: devWorkflowRun({
				graph: {
					schemaVersion: 1,
					nodes: [],
					edges: [
						{ from: "verdict", to: "ship", condition: { path: "$.passed", op: "eq", value: true } },
						{ from: "verdict", to: "fix", condition: { path: "$.passed", op: "eq", value: false } },
					],
				},
				nodes: [
					devWorkflowNodeRunSummary({ id: "n0", nodeKey: "verdict" }),
					devWorkflowNodeRunSummary({ id: "n1", nodeKey: "ship", label: "Ship it", status: "Running" }),
					devWorkflowNodeRunSummary({ id: "n2", nodeKey: "fix", label: "Fix it", status: "Pending" }),
				],
			}),
		});

		// The condition is rendered as stored. A client paraphrase the runtime evaluates differently is worse than none.
		expect(screen.getByTestId("dev-workflow-node-branch-condition-ship").textContent).toBe("$.passed eq true");
		// The taken branch is the successor that LEFT Pending — there is no conditionResult field on the wire.
		expect(screen.getByTestId("dev-workflow-node-branch-taken-ship")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-node-branch-taken-fix")).toBeNull();
	});

	it("still lists a branch whose node has not been created yet, rather than shrinking the graph", () => {
		renderPanel(devWorkflowNodeRunDetail({ nodeType: "Parallel", nodeKey: "fanout", label: "Fan out" }), {
			run: devWorkflowRun({
				graph: { schemaVersion: 1, nodes: [], edges: [{ from: "fanout", to: "implement" }] },
				nodes: [devWorkflowNodeRunSummary({ id: "n0", nodeKey: "fanout" })],
			}),
		});

		// A materialization template has no node-run row until its children exist; hiding it would be a smaller graph.
		expect(screen.getByTestId("dev-workflow-node-branch-implement").textContent).toContain("not created yet");
	});

	it("renders inputJson as raw text, because nothing parses it in v1", () => {
		renderPanel(devWorkflowNodeRunDetail({ inputJson: '{"workItemRequest":"Compare the options"}' }));

		expect(screen.getByTestId("dev-workflow-node-input").textContent).toBe('{"workItemRequest":"Compare the options"}');
	});
});

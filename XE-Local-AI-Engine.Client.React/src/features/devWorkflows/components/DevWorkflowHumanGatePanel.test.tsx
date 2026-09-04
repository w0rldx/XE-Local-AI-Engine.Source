// @vitest-environment jsdom

// The gate is the one place in this feature where a client bug is silently destructive rather than merely wrong, so
// three contracts are asserted directly: the panel renders ONLY when the runtime is actually asking, it offers only
// the decisions the server said are legal, and the `operationId` is stable across a retry of one attempt — the client
// half of the idempotency contract, and the single thing that turns a retried submit into a SECOND human act.

import { act, cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { ApiError } from "@/core/api/errors/ApiError";
import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import {
	type DevWorkflowDecisionSubmission,
	DevWorkflowHumanGatePanel,
} from "@/features/devWorkflows/components/DevWorkflowHumanGatePanel";
import type { DevWorkflowNodeRunDetailResponse } from "@/features/devWorkflows/models/DevWorkflowModels";
import { devWorkflowNodeRunDetail } from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

function gateNode(overrides: Partial<DevWorkflowNodeRunDetailResponse> = {}): DevWorkflowNodeRunDetailResponse {
	return devWorkflowNodeRunDetail({
		nodeKey: "approval",
		nodeType: "HumanGate",
		label: "Plan approval",
		status: "WaitingForApproval",
		pendingDecisionKind: "Approve",
		allowedDecisions: ["Approve", "Reject", "RequestChanges"],
		hasRejectBranch: true,
		instructions: "Approve the plan before implementation starts.",
		...overrides,
	});
}

interface PanelState {
	readonly nodeRun: DevWorkflowNodeRunDetailResponse;
	readonly isSubmitting?: boolean;
	readonly error?: unknown;
}

/**
 * The panel keeps the operation id in state, so the tests that matter here have to UPDATE its props rather than
 * re-mount it — a re-mount would mint a fresh id and quietly prove nothing. Testing Library's `rerender` replaces the
 * whole tree (dropping the providers), so the update goes through a stateful harness instead.
 */
function renderPanel(
	nodeRun: DevWorkflowNodeRunDetailResponse,
	options: {
		onDecide?: (s: DevWorkflowDecisionSubmission) => void;
		onShowArtifacts?: () => void;
		isSubmitting?: boolean;
		error?: unknown;
		artifactNameById?: ReadonlyMap<string, string>;
	} = {},
) {
	const onDecide = options.onDecide ?? vi.fn();
	const onShowArtifacts = options.onShowArtifacts ?? vi.fn();
	let update: (next: PanelState) => void = () => undefined;

	function Harness() {
		const [state, setState] = useState<PanelState>({ nodeRun, isSubmitting: options.isSubmitting, error: options.error });
		update = setState;
		return (
			<ConfirmProvider>
				<DevWorkflowHumanGatePanel
					nodeRun={state.nodeRun}
					isSubmitting={state.isSubmitting ?? false}
					error={state.error}
					artifactNameById={options.artifactNameById}
					onDecide={onDecide}
					onShowArtifacts={onShowArtifacts}
				/>
			</ConfirmProvider>
		);
	}

	const result = renderWithProviders(<Harness />);
	return { ...result, onDecide, onShowArtifacts, update: (next: PanelState) => act(() => update(next)) };
}

function conflict(conflictType: string, standingDecision?: string): ApiError {
	return new ApiError(409, {
		type: "about:blank",
		title: "Conflict",
		status: 409,
		detail: "already answered",
		conflictType,
		...(standingDecision ? { standingDecision } : {}),
	} as never);
}

describe("DevWorkflowHumanGatePanel", () => {
	afterEach(() => {
		cleanup();
	});

	it("renders the gate with its prompt when the runtime is asking", () => {
		renderPanel(gateNode());

		expect(screen.getByTestId("dev-workflow-gate-panel")).toBeDefined();
		expect(screen.getByText("Approve the plan before implementation starts.")).toBeDefined();
	});

	it("renders no controls at all when nothing is pending — it fails closed", () => {
		renderPanel(gateNode({ pendingDecisionKind: null }));

		expect(screen.queryByTestId("dev-workflow-gate-panel")).toBeNull();
		expect(screen.queryByTestId("dev-workflow-gate-Approve")).toBeNull();
	});

	it("renders no controls for a node that is neither awaiting approval nor blocked", () => {
		renderPanel(gateNode({ status: "Running" }));

		expect(screen.queryByTestId("dev-workflow-gate-panel")).toBeNull();
	});

	it("serves a Blocked node as an intervention, with the intervention decisions and its failure reason", () => {
		renderPanel(
			gateNode({
				status: "Blocked",
				pendingDecisionKind: "Retry",
				allowedDecisions: ["Retry", "Skip", "Abandon"],
				failureClass: "ToolCommandFailed",
				terminalReason: "dotnet build exited 1",
			}),
		);

		expect(screen.getByTestId("dev-workflow-gate-badge").textContent).toBe("needs your intervention");
		expect(screen.getByTestId("dev-workflow-gate-failure").textContent).toContain("A command failed");
		expect(screen.getByTestId("dev-workflow-gate-failure").textContent).toContain("dotnet build exited 1");
		expect(screen.getByTestId("dev-workflow-gate-Retry")).toBeDefined();
		expect(screen.getByTestId("dev-workflow-gate-Abandon")).toBeDefined();
	});

	it("promises the comment reaches the retried work only on the node types that actually read it", () => {
		const retryGate = { status: "Blocked", allowedDecisions: ["Retry", "Abandon"] };

		const agent = renderPanel(gateNode({ ...retryGate, nodeType: "Agent" }));

		expect(screen.getByTestId("dev-workflow-gate-panel").textContent).toContain("A retry reason is passed to the next attempt.");
		agent.unmount();

		const devTask = renderPanel(gateNode({ ...retryGate, nodeType: "DevTask" }));

		expect(screen.getByTestId("dev-workflow-gate-panel").textContent).toContain(
			"handed to the next coder round when the implementation is being reworked.",
		);
		devTask.unmount();

		// A Tool node never reads the comment, so it gets the plain hint and no promise at all.
		const tool = renderPanel(gateNode({ ...retryGate, nodeType: "Tool" }));

		expect(screen.getByTestId("dev-workflow-gate-panel").textContent).toContain("Required when you reject or ask for changes.");
		expect(screen.getByTestId("dev-workflow-gate-panel").textContent).not.toContain("A retry reason");
		tool.unmount();

		renderPanel(gateNode({ nodeType: "Agent", allowedDecisions: ["Approve", "Reject"] }));

		expect(screen.getByTestId("dev-workflow-gate-panel").textContent).not.toContain("A retry reason");
	});

	it("renders ONLY the decisions the server allows, so no button can come back a 409", () => {
		renderPanel(gateNode({ allowedDecisions: ["Approve", "Reject"] }));

		expect(screen.getByTestId("dev-workflow-gate-Approve")).toBeDefined();
		expect(screen.getByTestId("dev-workflow-gate-Reject")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-gate-RequestChanges")).toBeNull();
	});

	it("drops a decision token this client cannot render rather than guessing at it", () => {
		renderPanel(gateNode({ allowedDecisions: ["Approve", "Escalate"] }));

		expect(screen.getByTestId("dev-workflow-gate-Approve")).toBeDefined();
		expect(screen.getAllByTestId(/^dev-workflow-gate-[A-Z]/)).toHaveLength(1);
	});

	it("keeps Reject and Request changes disabled until a comment is typed", () => {
		renderPanel(gateNode());

		const reject = screen.getByTestId("dev-workflow-gate-Reject") as HTMLButtonElement;
		const requestChanges = screen.getByTestId("dev-workflow-gate-RequestChanges") as HTMLButtonElement;
		// Approving without a comment is legal; refusing without a reason is unactionable for the run.
		expect((screen.getByTestId("dev-workflow-gate-Approve") as HTMLButtonElement).disabled).toBe(false);
		expect(reject.disabled).toBe(true);
		expect(requestChanges.disabled).toBe(true);

		fireEvent.change(screen.getByTestId("dev-workflow-gate-comment"), { target: { value: "  " } });
		expect((screen.getByTestId("dev-workflow-gate-Reject") as HTMLButtonElement).disabled).toBe(true);

		fireEvent.change(screen.getByTestId("dev-workflow-gate-comment"), { target: { value: "the plan misses the migration" } });
		expect((screen.getByTestId("dev-workflow-gate-Reject") as HTMLButtonElement).disabled).toBe(false);
	});

	it("posts the decision with its comment and a generated operation id", async () => {
		const onDecide = vi.fn();
		renderPanel(gateNode(), { onDecide });

		fireEvent.click(screen.getByTestId("dev-workflow-gate-Approve"));

		await waitFor(() => expect(onDecide).toHaveBeenCalledTimes(1));
		const submission = onDecide.mock.calls[0]?.[0] as DevWorkflowDecisionSubmission;
		expect(submission.decision).toBe("Approve");
		expect(submission.comment).toBeUndefined();
		expect(submission.operationId).toMatch(/^[0-9a-f-]{36}$/i);
	});

	it("reuses the SAME operation id when a failed submit is retried", async () => {
		const onDecide = vi.fn();
		const { update } = renderPanel(gateNode(), { onDecide });

		fireEvent.click(screen.getByTestId("dev-workflow-gate-Approve"));
		await waitFor(() => expect(onDecide).toHaveBeenCalledTimes(1));

		// The submit failed; the panel re-renders with the error and the operator clicks again. A NEW id here would make
		// the retry look like a second human act, which the server refuses with the standing decision.
		update({ nodeRun: gateNode(), error: new Error("network") });
		fireEvent.click(screen.getByTestId("dev-workflow-gate-Approve"));
		await waitFor(() => expect(onDecide).toHaveBeenCalledTimes(2));

		const first = onDecide.mock.calls[0]?.[0] as DevWorkflowDecisionSubmission;
		const second = onDecide.mock.calls[1]?.[0] as DevWorkflowDecisionSubmission;
		expect(second.operationId).toBe(first.operationId);
	});

	it("mints a NEW operation id when the runtime asks again on a later attempt", async () => {
		const onDecide = vi.fn();
		const { update } = renderPanel(gateNode({ attempt: 1 }), { onDecide });

		fireEvent.click(screen.getByTestId("dev-workflow-gate-Approve"));
		await waitFor(() => expect(onDecide).toHaveBeenCalledTimes(1));

		update({ nodeRun: gateNode({ attempt: 2 }) });
		fireEvent.click(screen.getByTestId("dev-workflow-gate-Approve"));
		await waitFor(() => expect(onDecide).toHaveBeenCalledTimes(2));

		const first = onDecide.mock.calls[0]?.[0] as DevWorkflowDecisionSubmission;
		const second = onDecide.mock.calls[1]?.[0] as DevWorkflowDecisionSubmission;
		expect(second.operationId).not.toBe(first.operationId);
	});

	it("warns that Reject cancels the run when the gate has no rejection branch, and honours a cancelled confirm", async () => {
		const onDecide = vi.fn();
		renderPanel(gateNode({ hasRejectBranch: false }), { onDecide });

		fireEvent.change(screen.getByTestId("dev-workflow-gate-comment"), { target: { value: "wrong approach" } });
		fireEvent.click(screen.getByTestId("dev-workflow-gate-Reject"));

		const dialog = await screen.findByText("Reject and cancel this run?");
		expect(dialog).toBeDefined();
		fireEvent.click(screen.getByTestId("confirm-cancel"));

		await waitFor(() => expect(onDecide).not.toHaveBeenCalled());
	});

	it("asks no confirmation for a Reject that has a branch to follow", async () => {
		const onDecide = vi.fn();
		renderPanel(gateNode({ hasRejectBranch: true }), { onDecide });

		fireEvent.change(screen.getByTestId("dev-workflow-gate-comment"), { target: { value: "wrong approach" } });
		fireEvent.click(screen.getByTestId("dev-workflow-gate-Reject"));

		await waitFor(() => expect(onDecide).toHaveBeenCalledTimes(1));
		const submission = onDecide.mock.calls[0]?.[0] as DevWorkflowDecisionSubmission | undefined;
		expect(submission?.comment).toBe("wrong approach");
	});

	it("shows what the gate is about, by name, and jumps to it — evidence first, decision second", () => {
		const { onShowArtifacts } = renderPanel(gateNode({ consumedArtifactIds: ["artifact-1"] }), {
			artifactNameById: new Map([["artifact-1", "implementation-plan.md"]]),
		});

		const evidence = screen.getByTestId("dev-workflow-gate-evidence-artifact-1");
		expect(evidence.textContent).toBe("implementation-plan.md");
		fireEvent.click(evidence);
		expect(onShowArtifacts).toHaveBeenCalledTimes(1);
	});

	it("falls back to the artifact id when the run's artifact feed has not landed yet", () => {
		renderPanel(gateNode({ consumedArtifactIds: ["artifact-1"] }));

		expect(screen.getByTestId("dev-workflow-gate-evidence-artifact-1").textContent).toBe("artifact-1");
	});

	it("warns that Abandon records the node as FAILED, which is what the wire actually does", async () => {
		const onDecide = vi.fn();
		renderPanel(gateNode({ status: "Blocked", pendingDecisionKind: "Retry", allowedDecisions: ["Retry", "Abandon"] }), {
			onDecide,
		});

		fireEvent.click(screen.getByTestId("dev-workflow-gate-Abandon"));

		// The runtime lands an abandoned node run at Failed, not Cancelled, so the copy must not promise "cancelled".
		const body = await screen.findByText(/recorded as failed/i);
		expect(body).toBeDefined();
		fireEvent.click(screen.getByTestId("confirm-cancel"));
		await waitFor(() => expect(onDecide).not.toHaveBeenCalled());
	});

	it("disables every decision while a submit is in flight, and re-arms when the runtime asks again", () => {
		const { update } = renderPanel(gateNode());

		update({ nodeRun: gateNode(), isSubmitting: true });
		expect((screen.getByTestId("dev-workflow-gate-Approve") as HTMLButtonElement).disabled).toBe(true);
		expect((screen.getByTestId("dev-workflow-gate-Reject") as HTMLButtonElement).disabled).toBe(true);

		// The gate came back on a later attempt: the controls must arm again rather than stay stuck disabled.
		update({ nodeRun: gateNode({ attempt: 2 }), isSubmitting: false });
		expect((screen.getByTestId("dev-workflow-gate-Approve") as HTMLButtonElement).disabled).toBe(false);
	});

	it("renders the standing decision when a second human act is refused", () => {
		renderPanel(gateNode(), { error: conflict("DevWorkflowGateAlreadyDecided", "RequestChanges") });

		const alert = screen.getByTestId("dev-workflow-gate-already-decided");
		expect(alert.textContent).toContain("Request changes");
		expect(screen.queryByTestId("dev-workflow-gate-error")).toBeNull();
	});

	it("reads a wrong-state 409 as 'this has moved on' rather than as a hard error", () => {
		renderPanel(gateNode(), { error: conflict("DevWorkflowInvalidTransition") });

		expect(screen.getByTestId("dev-workflow-gate-stale")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-gate-error")).toBeNull();
	});

	it("keeps the comment draft on a failed submit so the operator can retry rather than retype", () => {
		const node = gateNode();
		const { update } = renderPanel(node);

		fireEvent.change(screen.getByTestId("dev-workflow-gate-comment"), { target: { value: "needs the migration" } });
		update({ nodeRun: node, error: new Error("network") });

		expect((screen.getByTestId("dev-workflow-gate-comment") as HTMLTextAreaElement).value).toBe("needs the migration");
	});

	it("lists prior decisions in sequence order, which is what explains a node on attempt 3", () => {
		renderPanel(
			gateNode({
				attempt: 3,
				decisions: [
					{ id: "d2", attempt: 2, decision: "RequestChanges", comment: "second", decidedBySubject: "admin", sequence: 20 },
					{ id: "d1", attempt: 1, decision: "RequestChanges", comment: "first", decidedBySubject: "admin", sequence: 10 },
				],
			}),
		);

		const rendered = screen.getByTestId("dev-workflow-gate-decisions").textContent ?? "";
		expect(rendered.indexOf("first")).toBeLessThan(rendered.indexOf("second"));
	});

	it("still shows the decision history once nothing is pending, without offering controls", () => {
		renderPanel(
			gateNode({
				status: "Succeeded",
				pendingDecisionKind: null,
				decisions: [{ id: "d1", attempt: 1, decision: "Approve", decidedBySubject: "admin", sequence: 10 }],
			}),
		);

		expect(screen.getByTestId("dev-workflow-gate-history")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-gate-Approve")).toBeNull();
	});
});

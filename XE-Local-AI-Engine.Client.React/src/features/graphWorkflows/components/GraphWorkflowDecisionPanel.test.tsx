// @vitest-environment jsdom

// The gate is the one place in this feature where a client bug is silently destructive rather than merely wrong, so
// what is pinned here is the wire contract of a decision: the panel renders only while the runtime is asking, the
// `operationId` is stable across a retry of ONE pending decision and fresh when the runtime asks again, a payload that
// is not an object never reaches the wire, and a 409 reports the decision that stands instead of submitting a second
// human act.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { useState } from "react";
import { act } from "react";
import { afterEach, describe, expect, it } from "vitest";

// Monaco is ~3 MB behind a lazy import and needs a layout engine jsdom does not have. The payload editor's CONTENT is
// what this file is about, not the editing surface, so the shared code editor stands in as a textarea.
import { vi } from "vitest";

vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({
	CodeEditor: ({
		value,
		onChange,
		"data-testid": testId,
	}: {
		value: string;
		onChange?: (next: string) => void;
		"data-testid"?: string;
	}) => <textarea data-testid={testId} value={value} onChange={(event) => onChange?.(event.currentTarget.value)} />,
}));

import { GraphWorkflowDecisionPanel } from "@/features/graphWorkflows/components/GraphWorkflowDecisionPanel";
import type { GraphWorkflowNodeRunResponse } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { graphWorkflowTestIds, pendingPauseNodeRun } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const runId = graphWorkflowTestIds.run;
const decidePath = localApiPath(`graph-workflows/runs/${runId}/nodes/review/decide`);

interface DecideBody {
	readonly operationId: string;
	readonly decision: string;
	readonly comment?: string;
	readonly payload?: unknown;
}

/** Records every decide body the panel actually posts — the only place the operation-id contract is observable. */
function recordDecides(answer: () => Response): DecideBody[] {
	const bodies: DecideBody[] = [];
	server.use(
		http.post(decidePath, async ({ request }) => {
			bodies.push((await request.json()) as DecideBody);
			return answer();
		}),
	);
	return bodies;
}

function serverError(): Response {
	return HttpResponse.json(
		{ type: "about:blank", title: "Server error", status: 500, detail: "The node fell over." },
		{ status: 500, headers: { "content-type": "application/problem+json" } },
	);
}

function alreadyDecided(standingDecision: string): Response {
	return HttpResponse.json(
		{
			type: "about:blank",
			title: "Conflict",
			status: 409,
			detail: "This gate was already decided.",
			conflictType: "GraphWorkflowGateAlreadyDecided",
			standingDecision,
		},
		{ status: 409, headers: { "content-type": "application/problem+json" } },
	);
}

/**
 * The panel keeps the operation id in state, so a test that changes the node run has to UPDATE its props rather than
 * re-mount it — a re-mount would mint a fresh id and prove nothing. Testing Library's `rerender` replaces the whole
 * tree (dropping the providers), so the update goes through a stateful harness instead.
 */
function renderPanel(
	nodeRun: GraphWorkflowNodeRunResponse,
	options: { requireComment?: boolean; allowedDecisions?: readonly ("Approve" | "Reject")[]; prompt?: string } = {},
) {
	let update: (next: GraphWorkflowNodeRunResponse) => void = () => undefined;

	function Harness() {
		const [current, setCurrent] = useState(nodeRun);
		update = setCurrent;
		return (
			<GraphWorkflowDecisionPanel
				runId={runId}
				nodeRun={current}
				allowedDecisions={options.allowedDecisions ?? ["Approve", "Reject"]}
				requireComment={options.requireComment ?? false}
				prompt={options.prompt}
			/>
		);
	}

	const result = renderWithProviders(<Harness />);
	return { ...result, update: (next: GraphWorkflowNodeRunResponse) => act(() => update(next)) };
}

describe("GraphWorkflowDecisionPanel", () => {
	afterEach(() => {
		cleanup();
	});

	it("renders no controls at all when the runtime is not asking — it fails closed", () => {
		renderPanel(pendingPauseNodeRun({ pendingDecisionKind: null }));

		expect(screen.queryByTestId("graph-workflow-decision-panel")).toBeNull();
		expect(screen.queryByTestId("graph-workflow-decision-Approve")).toBeNull();
	});

	it("renders the Pause prompt and one button per allowed decision", () => {
		renderPanel(pendingPauseNodeRun(), { prompt: "Approve the analysis?", allowedDecisions: ["Approve"] });

		expect(screen.getByTestId("graph-workflow-decision-prompt").textContent).toBe("Approve the analysis?");
		expect(screen.getByTestId("graph-workflow-decision-Approve")).toBeDefined();
		expect(screen.queryByTestId("graph-workflow-decision-Reject")).toBeNull();
	});

	it("reuses the SAME operation id when a failed submit is retried", async () => {
		const bodies = recordDecides(serverError);
		renderPanel(pendingPauseNodeRun());

		fireEvent.click(screen.getByTestId("graph-workflow-decision-Approve"));
		await waitFor(() => expect(bodies).toHaveLength(1));
		// The submit failed, so the panel re-arms and the operator clicks again. A NEW id here would make the retry a
		// SECOND human act, which the server refuses with the standing decision instead of replaying.
		await waitFor(() => expect((screen.getByTestId("graph-workflow-decision-Approve") as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(screen.getByTestId("graph-workflow-decision-Approve"));
		await waitFor(() => expect(bodies).toHaveLength(2));

		expect(bodies[1]?.operationId).toBe(bodies[0]?.operationId);
		expect(bodies[0]?.operationId).toMatch(/^[0-9a-f-]{36}$/i);
		expect(bodies[0]?.decision).toBe("Approve");
	});

	it("mints a NEW operation id when the runtime asks again on a later attempt", async () => {
		const bodies = recordDecides(serverError);
		const { update } = renderPanel(pendingPauseNodeRun({ attempt: 1 }));

		fireEvent.click(screen.getByTestId("graph-workflow-decision-Approve"));
		await waitFor(() => expect(bodies).toHaveLength(1));

		update(pendingPauseNodeRun({ attempt: 2 }));
		await waitFor(() => expect((screen.getByTestId("graph-workflow-decision-Approve") as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(screen.getByTestId("graph-workflow-decision-Approve"));
		await waitFor(() => expect(bodies).toHaveLength(2));

		expect(bodies[1]?.operationId).not.toBe(bodies[0]?.operationId);
	});

	it("requires a comment exactly when the Pause node says so", async () => {
		const bodies = recordDecides(serverError);
		const required = renderPanel(pendingPauseNodeRun(), { requireComment: true });

		expect((screen.getByTestId("graph-workflow-decision-Approve") as HTMLButtonElement).disabled).toBe(true);
		fireEvent.change(screen.getByTestId("graph-workflow-decision-comment"), { target: { value: "   " } });
		expect((screen.getByTestId("graph-workflow-decision-Approve") as HTMLButtonElement).disabled).toBe(true);
		fireEvent.change(screen.getByTestId("graph-workflow-decision-comment"), { target: { value: "looks right" } });
		fireEvent.click(screen.getByTestId("graph-workflow-decision-Approve"));
		await waitFor(() => expect(bodies).toHaveLength(1));
		expect(bodies[0]?.comment).toBe("looks right");
		required.unmount();

		// The same node without the flag: approving with nothing to say is legal.
		renderPanel(pendingPauseNodeRun(), { requireComment: false });
		expect((screen.getByTestId("graph-workflow-decision-Approve") as HTMLButtonElement).disabled).toBe(false);
	});

	it("blocks a payload that is not a JSON object rather than posting one the server would refuse", async () => {
		const bodies = recordDecides(serverError);
		renderPanel(pendingPauseNodeRun());

		fireEvent.click(screen.getByTestId("graph-workflow-decision-advanced-toggle"));
		fireEvent.change(screen.getByTestId("graph-workflow-decision-payload"), { target: { value: "[1, 2]" } });
		fireEvent.click(screen.getByTestId("graph-workflow-decision-Approve"));

		expect(screen.getByTestId("graph-workflow-decision-payload-error")).toBeDefined();
		expect(bodies).toHaveLength(0);

		fireEvent.change(screen.getByTestId("graph-workflow-decision-payload"), { target: { value: '{"ticket":"XE-1"}' } });
		fireEvent.click(screen.getByTestId("graph-workflow-decision-Approve"));
		await waitFor(() => expect(bodies).toHaveLength(1));
		expect(bodies[0]?.payload).toEqual({ ticket: "XE-1" });
	});

	it("re-arms when the runtime asks again, rather than staying closed on the last attempt's 409", async () => {
		recordDecides(() => alreadyDecided("Reject"));
		const { update } = renderPanel(pendingPauseNodeRun({ attempt: 1 }));

		fireEvent.click(screen.getByTestId("graph-workflow-decision-Approve"));
		await screen.findByTestId("graph-workflow-decision-already-decided");

		// The 409 belongs to the decision that was refused. A second Pause on the same node is a NEW question, so the
		// panel must not still be showing the old answer with every button dead.
		update(pendingPauseNodeRun({ attempt: 2 }));

		await waitFor(() => expect(screen.queryByTestId("graph-workflow-decision-already-decided")).toBeNull());
		expect((screen.getByTestId("graph-workflow-decision-Approve") as HTMLButtonElement).disabled).toBe(false);
	});

	it("reports the decision that stands on a 409 and does not submit again", async () => {
		const bodies = recordDecides(() => alreadyDecided("Reject"));
		renderPanel(pendingPauseNodeRun());

		fireEvent.click(screen.getByTestId("graph-workflow-decision-Approve"));

		const alert = await screen.findByTestId("graph-workflow-decision-already-decided");
		expect(alert.textContent).toContain("Reject");
		expect(screen.queryByTestId("graph-workflow-decision-error")).toBeNull();
		// Every further click would earn the same 409, so the controls are closed rather than left to be re-clicked.
		expect((screen.getByTestId("graph-workflow-decision-Approve") as HTMLButtonElement).disabled).toBe(true);
		fireEvent.click(screen.getByTestId("graph-workflow-decision-Approve"));
		expect(bodies).toHaveLength(1);
	});
});

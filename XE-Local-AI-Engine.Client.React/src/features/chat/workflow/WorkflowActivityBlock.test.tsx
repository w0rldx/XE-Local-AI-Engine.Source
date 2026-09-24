// @vitest-environment jsdom

// The block is built from durable state only, so what is pinned here is that each document lands where the operator
// reads it: the DecisionModel's choice as routing, the ChatInput's question and answer, the intermediate text behind a
// disclosure, and the publish marker off the event trail.

import { QueryClient } from "@tanstack/react-query";
import { fireEvent, screen, within } from "@testing-library/react";
import i18next from "i18next";
import { HttpResponse, http } from "msw";
import { describe, expect, it } from "vitest";

import { getGraphWorkflowNodeRunQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { WorkflowActivityBlock } from "@/features/chat/workflow/WorkflowActivityBlock";
import {
	agentNodeRunDetail,
	chatGraph,
	chatWorkflowEvents,
	graphWorkflowRun,
	graphWorkflowRunEvent,
	graphWorkflowRunSummary,
	graphWorkflowTestGuid,
	graphWorkflowTestIds,
	makeNodeRun,
} from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const runId = graphWorkflowTestIds.run;

const outputs: Record<string, { kind: string; output: unknown }> = {
	ask: { kind: "ChatInput", output: { decision: "Answer", text: "A CLI" } },
	classify: { kind: "DecisionModel", output: { choice: "coding", provider: "llm" } },
	code: { kind: "LlmCall", output: { text: "fn main() {}" } },
};

function routes() {
	server.use(
		jsonRoute(
			"get",
			`graph-workflows/runs/${runId}`,
			graphWorkflowRun({
				run: graphWorkflowRunSummary({ status: "Completed" }),
				graph: chatGraph,
				nodeRuns: [
					makeNodeRun({ id: graphWorkflowTestGuid(1), nodeKey: "start", kind: "Start", status: "Succeeded" }),
					makeNodeRun({ id: graphWorkflowTestGuid(2), nodeKey: "ask", kind: "ChatInput", status: "Succeeded" }),
					makeNodeRun({ id: graphWorkflowTestGuid(3), nodeKey: "classify", kind: "DecisionModel", status: "Succeeded" }),
					makeNodeRun({ id: graphWorkflowTestGuid(4), nodeKey: "code", kind: "LlmCall", status: "Succeeded" }),
					makeNodeRun({ id: graphWorkflowTestGuid(5), nodeKey: "done", kind: "End", status: "Succeeded" }),
				],
			}),
		),
		jsonRoute("get", `graph-workflows/runs/${runId}/events`, chatWorkflowEvents()),
		http.get(localApiPath(`graph-workflows/runs/${runId}/nodes/:nodeKey`), ({ params }) => {
			const nodeKey = String(params["nodeKey"]);
			const entry = outputs[nodeKey];
			return HttpResponse.json(
				agentNodeRunDetail({
					nodeKey,
					kind: entry?.kind ?? "Start",
					output: { status: "succeeded", attempt: 1, branch: null, output: entry?.output ?? {} },
				}),
			);
		}),
	);
}

describe("WorkflowActivityBlock", () => {
	it("shows routing, the question and answer, the publish marker, and the intermediate output behind a disclosure", async () => {
		routes();
		renderWithProviders(<WorkflowActivityBlock runId={runId} workflowName="Support triage" />);

		const block = await screen.findByTestId(`chat-workflow-activity-${runId}`);
		expect(within(block).getByText("Workflow: Support triage")).toBeTruthy();
		expect((await within(block).findByTestId("chat-workflow-activity-route-classify")).textContent).toBe("→ coding");
		expect((await within(block).findByTestId("chat-workflow-activity-input-ask")).textContent).toBe(
			"Asked: What should I build? — Answer: A CLI",
		);
		expect(within(block).getByTestId("chat-workflow-activity-published-code").textContent).toBe("Published to chat");

		const toggle = await within(block).findByTestId("chat-workflow-activity-output-toggle-code");
		expect(toggle.getAttribute("aria-expanded")).toBe("false");
		fireEvent.click(toggle);
		expect(toggle.getAttribute("aria-expanded")).toBe("true");
		expect(within(block).getByTestId("chat-workflow-activity-output-code").textContent).toBe("fn main() {}");
	});

	it("reads a truncated event trail to the end on its own, so a later page's publish marker still lands", async () => {
		routes();
		const requestedCursors: string[] = [];
		const [created, completed, published] = chatWorkflowEvents().events;
		if (!(created && completed && published)) {
			throw new Error("The event fixture changed shape.");
		}
		server.use(
			http.get(localApiPath(`graph-workflows/runs/${runId}/events`), ({ request }) => {
				const afterSeq = new URL(request.url).searchParams.get("afterSeq") ?? "0";
				requestedCursors.push(afterSeq);
				return HttpResponse.json(
					afterSeq === "0"
						? chatWorkflowEvents({ events: [created, completed], lastSeq: 2, replayTruncated: true })
						: chatWorkflowEvents({ events: [published], lastSeq: 3, replayTruncated: false }),
				);
			}),
		);

		renderWithProviders(<WorkflowActivityBlock runId={runId} workflowName="Support triage" />);

		expect((await screen.findByTestId("chat-workflow-activity-published-code")).textContent).toBe(
			i18next.t("pages.chat.workflow.activity.published"),
		);
		expect(requestedCursors).toEqual(["0", "2"]);
	});

	it("says which attachments an Agent turn went without", async () => {
		outputs["code"] = { kind: "LlmCall", output: { text: "fn main() {}", attachmentsSkipped: ["a.pdf", "b.png"] } };
		try {
			routes();
			renderWithProviders(<WorkflowActivityBlock runId={runId} workflowName="Support triage" />);

			expect((await screen.findByTestId("chat-workflow-activity-attachments-skipped-code")).textContent).toBe(
				i18next.t("pages.chat.workflow.activity.attachmentsSkipped", { names: "a.pdf, b.png" }),
			);
			expect(screen.queryByTestId("chat-workflow-activity-attachments-skipped-classify")).toBeNull();
		} finally {
			outputs["code"] = { kind: "LlmCall", output: { text: "fn main() {}" } };
		}
	});

	it("replaces a cached Failed node document once the node run reports a later attempt", async () => {
		server.use(
			jsonRoute(
				"get",
				`graph-workflows/runs/${runId}`,
				graphWorkflowRun({
					run: graphWorkflowRunSummary({ status: "Completed" }),
					graph: chatGraph,
					nodeRuns: [makeNodeRun({ nodeKey: "code", kind: "LlmCall", status: "Succeeded", attempt: 2 })],
				}),
			),
			jsonRoute("get", `graph-workflows/runs/${runId}/events`, chatWorkflowEvents()),
			jsonRoute(
				"get",
				`graph-workflows/runs/${runId}/nodes/code`,
				agentNodeRunDetail({
					nodeKey: "code",
					kind: "LlmCall",
					attempt: 2,
					output: { status: "succeeded", attempt: 2, branch: null, output: { text: "fn main() {}" } },
				}),
			),
		);
		// A cache that outlives the unmount, holding the document read while attempt 1 had failed.
		const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false } } });
		queryClient.setQueryData(
			getGraphWorkflowNodeRunQueryKey({ path: { runId, nodeKey: "code" } }),
			agentNodeRunDetail({ nodeKey: "code", kind: "LlmCall", status: "Failed", attempt: 1, error: "boom", output: null }),
		);

		renderWithProviders(<WorkflowActivityBlock runId={runId} workflowName="Support triage" />, { queryClient });

		fireEvent.click(await screen.findByTestId("chat-workflow-activity-output-toggle-code"));
		expect(screen.getByTestId("chat-workflow-activity-output-code").textContent).toBe("fn main() {}");
		queryClient.clear();
	});

	it("says a parked ChatInput waits for your input, not for a decision", async () => {
		server.use(
			jsonRoute(
				"get",
				`graph-workflows/runs/${runId}`,
				graphWorkflowRun({
					run: graphWorkflowRunSummary({ status: "WaitingForApproval" }),
					graph: chatGraph,
					nodeRuns: [
						makeNodeRun({ id: graphWorkflowTestGuid(1), nodeKey: "start", kind: "Start", status: "Succeeded" }),
						makeNodeRun({
							id: graphWorkflowTestGuid(2),
							nodeKey: "ask",
							kind: "ChatInput",
							status: "WaitingForApproval",
							completedAtUtc: null,
						}),
					],
				}),
			),
			jsonRoute("get", `graph-workflows/runs/${runId}/events`, chatWorkflowEvents()),
		);

		renderWithProviders(<WorkflowActivityBlock runId={runId} workflowName="Support triage" />);

		const row = await screen.findByTestId("chat-workflow-activity-node-ask");
		expect(row.textContent).toContain(i18next.t("pages.graphWorkflows.runStatus.WaitingForInput"));
		expect(row.textContent).not.toContain(i18next.t("pages.graphWorkflows.nodeStatus.WaitingForApproval"));
	});

	it("renders the live output under the running node's row only", async () => {
		server.use(
			jsonRoute(
				"get",
				`graph-workflows/runs/${runId}`,
				graphWorkflowRun({
					run: graphWorkflowRunSummary({ status: "Running" }),
					graph: chatGraph,
					nodeRuns: [
						makeNodeRun({ id: graphWorkflowTestGuid(1), nodeKey: "start", kind: "Start", status: "Succeeded" }),
						makeNodeRun({
							id: graphWorkflowTestGuid(4),
							nodeKey: "code",
							kind: "LlmCall",
							status: "Running",
							completedAtUtc: null,
							invocationId: graphWorkflowTestGuid(40),
						}),
					],
				}),
			),
			jsonRoute("get", `graph-workflows/runs/${runId}/events`, chatWorkflowEvents()),
		);

		renderWithProviders(
			<WorkflowActivityBlock
				runId={runId}
				workflowName="Support triage"
				liveNodeKey="code"
				liveDetail={<span data-testid="live-detail">live</span>}
			/>,
		);

		const row = await screen.findByTestId("chat-workflow-activity-node-code");
		expect(within(row).getByTestId("live-detail")).toBeTruthy();
		expect(screen.getAllByTestId("live-detail")).toHaveLength(1);
	});

	it("marks a steered node and a steer that arrived after the node finished, without calling it a retry", async () => {
		server.use(
			jsonRoute(
				"get",
				`graph-workflows/runs/${runId}`,
				graphWorkflowRun({
					run: graphWorkflowRunSummary({ status: "Running" }),
					graph: chatGraph,
					nodeRuns: [makeNodeRun({ nodeKey: "code", kind: "LlmCall", status: "Running", completedAtUtc: null })],
				}),
			),
			jsonRoute(
				"get",
				`graph-workflows/runs/${runId}/events`,
				chatWorkflowEvents({
					events: [
						graphWorkflowRunEvent({ seq: 1, eventType: "node.steered", nodeKey: "code", detail: { message: "Use Rust" } }),
						graphWorkflowRunEvent({ seq: 2, eventType: "node.steer-ignored", nodeKey: "code" }),
					],
					lastSeq: 2,
				}),
			),
		);

		renderWithProviders(<WorkflowActivityBlock runId={runId} workflowName="Support triage" />);

		await screen.findByTestId("chat-workflow-activity-node-code");
		const markers = await screen.findAllByTestId("chat-workflow-activity-steer-code");
		expect(markers.map((marker) => marker.textContent)).toEqual([
			i18next.t("pages.chat.workflow.activity.steered", { message: "Use Rust" }),
			i18next.t("pages.chat.workflow.activity.steerIgnored"),
		]);
	});
});

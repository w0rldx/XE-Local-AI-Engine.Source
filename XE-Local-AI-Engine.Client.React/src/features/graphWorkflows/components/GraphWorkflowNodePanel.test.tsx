// @vitest-environment jsdom

// The node panel is where an operator finds out what a node actually did, so what is pinned is the reading of the
// output envelope rather than the chrome: an Agent's prose above its raw document, a Tool's result in both the shapes
// the wire uses, the one line that says a structural node's output is SUPPOSED to look like its predecessor's — and
// currency-versus-failure, the precedent that a node which failed stays failed after the run has moved on.

import { QueryClient } from "@tanstack/react-query";
import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { act, useState } from "react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

// Monaco is ~3 MB behind a lazy import and needs a layout engine jsdom does not have; the documents' CONTENT is what
// this file is about, so the shared code editor stands in as a textarea.
vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({
	CodeEditor: ({ value, "data-testid": testId }: { value: string; "data-testid"?: string }) => (
		<textarea data-testid={testId} readOnly={true} value={value} />
	),
}));

import { getGraphWorkflowNodeRunQueryKey } from "@/core/api/generated/@tanstack/react-query.gen";
import { GraphWorkflowNodePanel } from "@/features/graphWorkflows/components/GraphWorkflowNodePanel";
import type { GraphWorkflowNodeRunResponse } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { agentNodeRunDetail, graphWorkflowTestIds } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const runId = graphWorkflowTestIds.run;

/** The response schema types both ids as GUIDs, so a served row carries them even where the fixture reads better. */
function detail(overrides: Partial<GraphWorkflowNodeRunResponse>): GraphWorkflowNodeRunResponse {
	return agentNodeRunDetail({ id: graphWorkflowTestIds.nodeRun, invocationId: null, ...overrides });
}

function nodeRoute(nodeKey: string, body: GraphWorkflowNodeRunResponse): void {
	server.use(http.get(localApiPath(`graph-workflows/runs/${runId}/nodes/${nodeKey}`), () => HttpResponse.json(body)));
}

function renderPanel(nodeKey: string, runStatus: string) {
	return renderWithProviders(
		<GraphWorkflowNodePanel runId={runId} nodeKey={nodeKey} runStatus={runStatus} onClose={() => undefined} />,
	);
}

describe("GraphWorkflowNodePanel", () => {
	afterEach(() => {
		cleanup();
	});

	it("keeps a failed node failed after the run has moved on and completed", async () => {
		nodeRoute(
			"lookup",
			detail({
				nodeKey: "lookup",
				kind: "Tool",
				status: "Failed",
				attempt: 3,
				failureClass: "AttemptsExhausted",
				error: "read_file did not answer within the node timeout.",
				output: null,
			}),
		);

		renderPanel("lookup", "Completed");

		// Two axes: the run's own status says Completed, and this node still reports what IT did.
		expect((await screen.findByTestId("graph-workflow-node-panel-status")).textContent).toBe("Failed");
		expect(screen.getByTestId("graph-workflow-node-panel-failure").textContent).toContain("Out of attempts");
		expect(screen.getByTestId("graph-workflow-node-panel-moved-on")).toBeDefined();
		expect(screen.getByTestId("graph-workflow-node-panel-attempt").textContent).toBe("attempt 3");
	});

	it("shows an Agent's answer as prose ABOVE the raw document, not buried inside it", async () => {
		nodeRoute("analyze", detail({}));

		renderPanel("analyze", "Running");

		const text = await screen.findByTestId("graph-workflow-node-panel-agent-text");
		expect(text.textContent).toBe("The release removes two endpoints, so a human should confirm.");
		const raw = screen.getByTestId("graph-workflow-node-panel-output") as HTMLTextAreaElement;
		// The raw envelope stays available underneath — the prose is a reading of it, not a replacement.
		expect(raw.value).toContain('"requiresReview": true');
		expect(text.compareDocumentPosition(raw) & Node.DOCUMENT_POSITION_FOLLOWING).toBeTruthy();
	});

	it("renders a Tool result whether the tool answered with a document or with a bare string", async () => {
		nodeRoute(
			"lookup",
			detail({
				nodeKey: "lookup",
				kind: "Tool",
				output: { status: "succeeded", attempt: 1, output: { result: { files: ["notes.md"] } } },
			}),
		);
		const structured = renderPanel("lookup", "Running");

		expect(((await screen.findByTestId("graph-workflow-node-panel-tool-result")) as HTMLTextAreaElement).value).toContain(
			"notes.md",
		);
		structured.unmount();

		nodeRoute(
			"lookup",
			detail({
				nodeKey: "lookup",
				kind: "Tool",
				output: { status: "succeeded", attempt: 1, output: { result: "the file was empty" } },
			}),
		);
		renderPanel("lookup", "Running");

		// A string result is the answer itself, so it reads as text rather than through the JSON viewer — and it is
		// not JSON-encoded on the way there, since the quotes would read as part of the answer.
		await waitFor(() =>
			expect(screen.getByTestId("graph-workflow-node-panel-tool-result").textContent).toBe("the file was empty"),
		);
		expect(screen.getByTestId("graph-workflow-node-panel-tool-result").tagName).not.toBe("TEXTAREA");
	});

	it("says out loud that a Condition passes its input through, so an identical output is not read as a bug", async () => {
		nodeRoute(
			"check",
			detail({ nodeKey: "check", kind: "Condition", output: { status: "succeeded", attempt: 1, branch: "true", output: {} } }),
		);

		renderPanel("check", "Running");

		expect((await screen.findByTestId("graph-workflow-node-panel-pass-through")).textContent).toContain("unchanged");
	});

	it("says what a Join's output is keyed by, which the document itself does not", async () => {
		nodeRoute("merge", detail({ nodeKey: "merge", kind: "Join", output: { status: "succeeded", attempt: 1, output: {} } }));

		renderPanel("merge", "Running");

		expect((await screen.findByTestId("graph-workflow-node-panel-pass-through")).textContent).toContain("keyed by");
	});

	it("starts each node on its Output tab rather than inheriting the last node's choice", async () => {
		// Both node runs are already in the cache, so switching nodes never passes through the loading state. That is
		// the case the tab actually has to be reset for: with a cold cache the panel unmounts and resets by accident.
		const queryClient = new QueryClient({ defaultOptions: { queries: { retry: false, staleTime: Number.POSITIVE_INFINITY } } });
		for (const [nodeKey, body] of [
			["analyze", detail({})],
			["check", detail({ nodeKey: "check", kind: "Condition" })],
		] as const) {
			queryClient.setQueryData(getGraphWorkflowNodeRunQueryKey({ path: { runId, nodeKey } }), body);
		}
		let select: (nodeKey: string) => void = () => undefined;

		function Harness() {
			const [nodeKey, setNodeKey] = useState("analyze");
			select = setNodeKey;
			return <GraphWorkflowNodePanel runId={runId} nodeKey={nodeKey} runStatus="Running" onClose={() => undefined} />;
		}

		renderWithProviders(<Harness />, { queryClient });

		fireEvent.click(await screen.findByTestId("graph-workflow-node-panel-tab-input"));
		expect(screen.getByTestId("graph-workflow-node-panel-tab-input").getAttribute("data-active")).toBe("true");

		// A different node is a different question, and the Input tab was an answer to the last one.
		act(() => select("check"));

		await waitFor(() =>
			expect(screen.getByTestId("graph-workflow-node-panel-tab-output").getAttribute("data-active")).toBe("true"),
		);
	});

	it("renders the load failure rather than an empty pane when the node cannot be read", async () => {
		server.use(
			http.get(localApiPath(`graph-workflows/runs/${runId}/nodes/analyze`), () =>
				HttpResponse.json(
					{ type: "about:blank", title: "Not found", status: 404, detail: "That node run does not exist." },
					{ status: 404, headers: { "content-type": "application/problem+json" } },
				),
			),
		);

		renderPanel("analyze", "Running");

		expect((await screen.findByTestId("graph-workflow-node-panel-error")).textContent).toContain(
			"That node run does not exist.",
		);
	});
});

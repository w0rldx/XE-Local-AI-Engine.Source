// @vitest-environment jsdom

// Starting a run is the one write on this surface that can happen TWICE by accident, so the idempotency half is
// pinned first: one `requestId` per dialog open, reused across a retry, fresh on the next open. The rest is the three
// ways this dialog refuses to start a run that would lie — an unsaved canvas, an oversized input, and JSON that is not
// JSON — plus the version it names, which is the operator's only proof that the graph they just saved is the one about
// to run.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

// Monaco is ~3 MB behind a lazy import and needs a layout engine jsdom does not have; the input's CONTENT is what this
// file is about, so the shared code editor stands in as a textarea.
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

import { GraphWorkflowStartRunDialog } from "@/features/graphWorkflows/components/GraphWorkflowStartRunDialog";
import { GRAPH_WORKFLOW_MAX_RUN_INPUT_BYTES } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { graphWorkflowTestIds } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const definitionId = graphWorkflowTestIds.definition;
const definition = { id: definitionId, name: "Analyze → review → read", version: 4 };
const startPath = localApiPath(`graph-workflows/definitions/${definitionId}/runs`);

interface StartBody {
	readonly requestId: string;
	readonly input?: unknown;
	readonly definitionVersion?: number | null;
}

/** Records every start body, which is the only place the one-id-per-open contract is observable. */
function recordStarts(answers: (() => Response)[]): StartBody[] {
	const bodies: StartBody[] = [];
	server.use(
		http.post(startPath, async ({ request }) => {
			bodies.push((await request.json()) as StartBody);
			const answer = answers[bodies.length - 1] ?? answers.at(-1);
			return answer?.() ?? HttpResponse.json({ runId: graphWorkflowTestIds.run }, { status: 202 });
		}),
	);
	return bodies;
}

function accepted(): Response {
	return HttpResponse.json({ runId: graphWorkflowTestIds.run }, { status: 202 });
}

function serverError(): Response {
	return HttpResponse.json(
		{ type: "about:blank", title: "Server error", status: 500, detail: "The dispatcher was not listening." },
		{ status: 500, headers: { "content-type": "application/problem+json" } },
	);
}

function renderDialog(options: { isDirty?: boolean; defaultInput?: unknown; onStarted?: (runId: string) => void } = {}) {
	return renderWithProviders(
		<GraphWorkflowStartRunDialog
			opened={true}
			onClose={() => undefined}
			definition={definition}
			defaultInput={options.defaultInput ?? { request: "Summarise the release notes." }}
			isDirty={options.isDirty ?? false}
			onStarted={options.onStarted ?? (() => undefined)}
		/>,
	);
}

describe("GraphWorkflowStartRunDialog", () => {
	afterEach(() => {
		cleanup();
	});

	it("names the version it is about to run and sends that same version", async () => {
		const bodies = recordStarts([accepted]);
		renderDialog();

		expect(screen.getByTestId("graph-workflow-start-run-version").textContent).toBe(
			"Runs version 4 of Analyze → review → read",
		);
		fireEvent.click(screen.getByTestId("graph-workflow-start-run-submit"));

		await waitFor(() => expect(bodies).toHaveLength(1));
		expect(bodies[0]?.definitionVersion).toBe(4);
		expect(bodies[0]?.input).toEqual({ request: "Summarise the release notes." });
	});

	it("hands the caller the run id off the 202 body", async () => {
		recordStarts([accepted]);
		const onStarted = vi.fn();
		renderDialog({ onStarted });

		fireEvent.click(screen.getByTestId("graph-workflow-start-run-submit"));

		await waitFor(() => expect(onStarted).toHaveBeenCalledWith(graphWorkflowTestIds.run));
	});

	it("reuses the SAME request id when a failed start is retried, and mints a new one on the next open", async () => {
		const bodies = recordStarts([serverError, accepted]);
		const first = renderDialog();

		fireEvent.click(screen.getByTestId("graph-workflow-start-run-submit"));
		await waitFor(() => expect(bodies).toHaveLength(1));
		// The dispatcher never answered; a NEW id on the retry would start a SECOND run of the same graph.
		await waitFor(() => expect((screen.getByTestId("graph-workflow-start-run-submit") as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(screen.getByTestId("graph-workflow-start-run-submit"));
		await waitFor(() => expect(bodies).toHaveLength(2));

		expect(bodies[1]?.requestId).toBe(bodies[0]?.requestId);
		expect(bodies[0]?.requestId).toMatch(/^[0-9a-f-]{36}$/i);
		first.unmount();

		// A second open is a second intent to run, so it gets its own id rather than replaying the first.
		renderDialog();
		fireEvent.click(screen.getByTestId("graph-workflow-start-run-submit"));
		await waitFor(() => expect(bodies).toHaveLength(3));
		expect(bodies[2]?.requestId).not.toBe(bodies[0]?.requestId);
	});

	it("blocks Start with the Save-first hint while the editor is dirty, and arms it once it is not", () => {
		const dirty = renderDialog({ isDirty: true });

		expect((screen.getByTestId("graph-workflow-start-run-submit") as HTMLButtonElement).disabled).toBe(true);
		// The run executes the SAVED graph, so starting with unsaved edits would silently run a different one.
		expect(screen.getByTestId("graph-workflow-start-run-dirty").textContent).toContain("Save the graph first");
		dirty.unmount();

		renderDialog({ isDirty: false });

		expect((screen.getByTestId("graph-workflow-start-run-submit") as HTMLButtonElement).disabled).toBe(false);
		expect(screen.queryByTestId("graph-workflow-start-run-dirty")).toBeNull();
	});

	it("counts the input in UTF-8 bytes and blocks Start past the node's cap", async () => {
		const bodies = recordStarts([accepted]);
		renderDialog();

		const oversized = JSON.stringify({ text: "ü".repeat(GRAPH_WORKFLOW_MAX_RUN_INPUT_BYTES) });
		fireEvent.change(screen.getByTestId("graph-workflow-start-run-input"), { target: { value: oversized } });

		// Two bytes per "ü": a length check would have let this through and earned a 400 after the paste.
		expect(screen.getByTestId("graph-workflow-start-run-bytes").textContent).toContain(
			String(GRAPH_WORKFLOW_MAX_RUN_INPUT_BYTES),
		);
		expect(screen.getByTestId("graph-workflow-start-run-too-large")).toBeDefined();
		expect((screen.getByTestId("graph-workflow-start-run-submit") as HTMLButtonElement).disabled).toBe(true);

		fireEvent.change(screen.getByTestId("graph-workflow-start-run-input"), { target: { value: '{"ok":true}' } });
		expect((screen.getByTestId("graph-workflow-start-run-submit") as HTMLButtonElement).disabled).toBe(false);
		fireEvent.click(screen.getByTestId("graph-workflow-start-run-submit"));
		await waitFor(() => expect(bodies).toHaveLength(1));
	});

	it("blocks Start on input that is not valid JSON rather than posting a string", () => {
		renderDialog();

		fireEvent.change(screen.getByTestId("graph-workflow-start-run-input"), { target: { value: "{not json" } });

		expect(screen.getByTestId("graph-workflow-start-run-invalid-json")).toBeDefined();
		expect((screen.getByTestId("graph-workflow-start-run-submit") as HTMLButtonElement).disabled).toBe(true);
	});

	it("reads a 409 as 'this definition moved on' rather than as a start to retry", async () => {
		recordStarts([
			() =>
				HttpResponse.json(
					{
						type: "about:blank",
						title: "Conflict",
						status: 409,
						detail: "The definition version does not match.",
						conflictType: "GraphWorkflowRunConflict",
					},
					{ status: 409, headers: { "content-type": "application/problem+json" } },
				),
		]);
		renderDialog();

		fireEvent.click(screen.getByTestId("graph-workflow-start-run-submit"));

		expect((await screen.findByTestId("graph-workflow-start-run-stale")).textContent).toContain("saved again");
		expect(screen.queryByTestId("graph-workflow-start-run-error")).toBeNull();
	});
});

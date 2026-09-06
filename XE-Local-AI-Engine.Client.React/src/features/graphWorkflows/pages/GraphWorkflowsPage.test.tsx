// @vitest-environment jsdom

// The page is composition, so what is pinned here is the WIRING nothing else can hold: which mode one search param
// picks, that a definition load reaches the editor's dirty check, that Save is gated on both dirtiness and the
// server's answer, and that a Pause node's decision buttons come from the DEFINITION rather than from the panel's
// fallback. Every component below is real; only the three things jsdom cannot host are stood in for.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

// React Flow measures its container and jsdom reports 0×0, so a real `<ReactFlow>` paints no viewport and no card —
// the same note the canvas and run-graph tests carry. The editor hook's own `applyNodeChanges`/`layout` stay real.
vi.mock("@xyflow/react", async (importOriginal) => ({
	...(await importOriginal<typeof import("@xyflow/react")>()),
	Background: () => null,
	Controls: () => null,
	Handle: () => null,
	ReactFlowProvider: ({ children }: { readonly children: React.ReactNode }) => children,
	useReactFlow: () => ({ screenToFlowPosition: () => ({ x: 0, y: 0 }), fitView: () => Promise.resolve(true) }),
	ReactFlow: () => <div data-testid="react-flow" />,
}));

// Monaco is ~3 MB behind a lazy import and wants a layout engine. The documents are other files' subject.
vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({
	CodeEditor: ({ value, "data-testid": testId }: { value: string; "data-testid"?: string }) => (
		<textarea data-testid={testId} readOnly={true} value={value} />
	),
}));

// `useUnsavedChangesGuard` reaches for router context; its own file tests the blocking. Here it only has to be inert,
// so the page can render without the async memory-router mount.
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useBlocker: () => ({ status: "idle", proceed: undefined, reset: undefined }),
}));

// The hub is exercised in `useGraphWorkflowRunHub.test.tsx`; here it only has to not reach for a real socket.
vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: () => ({
		connection: { state: "Disconnected", on: vi.fn(), off: vi.fn(), invoke: vi.fn(async () => undefined) },
		whenStarted: Promise.resolve(),
		onReconnected: () => vi.fn(),
		onReconnecting: () => vi.fn(),
		onClosed: () => vi.fn(),
		release: vi.fn(),
	}),
}));

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import type { GraphWorkflowGraph, GraphWorkflowSelection } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { GraphWorkflowsPage } from "@/features/graphWorkflows/pages/GraphWorkflowsPage";
import {
	eightNodeGraph,
	graphWorkflowDefinition,
	graphWorkflowDefinitionSummary,
	graphWorkflowRun,
	graphWorkflowRunSummary,
	graphWorkflowTestIds,
	pendingPauseNodeRun,
} from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const definitionId = graphWorkflowTestIds.definition;
const runId = graphWorkflowTestIds.run;

function editorRoutes(graph: GraphWorkflowGraph = eightNodeGraph) {
	return [
		jsonRoute("get", "graph-workflows/definitions", { definitions: [graphWorkflowDefinitionSummary()] }),
		jsonRoute("get", `graph-workflows/definitions/${definitionId}`, graphWorkflowDefinition({ graph })),
	];
}

function runViewRoutes(graph: GraphWorkflowGraph = eightNodeGraph) {
	return [
		jsonRoute("get", `graph-workflows/runs/${runId}`, graphWorkflowRun()),
		jsonRoute("get", "graph-workflows/runs", { runs: [graphWorkflowRunSummary()] }),
		jsonRoute("get", `graph-workflows/definitions/${definitionId}`, graphWorkflowDefinition({ graph })),
	];
}

/** The server's structural check. `valid: false` is what puts an error on a node, so both answers are needed. */
function validateRoute(body: { valid: boolean; errors?: { key: string | null; message: string }[] }) {
	return http.post(localApiPath("graph-workflows/definitions/validate"), () =>
		HttpResponse.json({ valid: body.valid, errors: body.errors ?? [], nodeCount: 8 }),
	);
}

function renderPage(selection: GraphWorkflowSelection) {
	const onSelectionChange = vi.fn();
	renderWithProviders(
		<ConfirmProvider>
			<GraphWorkflowsPage selection={selection} onSelectionChange={onSelectionChange} />
		</ConfirmProvider>,
	);
	return { onSelectionChange };
}

/** Auto-arrange is the one edit that dirties the canvas without making the graph invalid, so Save stays reachable. */
async function openAndDirty(): Promise<void> {
	await waitFor(() => {
		expect(screen.getByTestId("gw-page-definition-name").textContent).toBe("Analyze → review → read");
	});
	fireEvent.click(screen.getByTestId("graph-workflow-auto-arrange"));
	await waitFor(() => {
		expect(screen.getByTestId<HTMLButtonElement>("gw-page-save").disabled).toBe(false);
	});
}

describe("GraphWorkflowsPage", () => {
	afterEach(() => {
		cleanup();
	});

	it("shows the editor and its definition list when the selection carries no run", async () => {
		server.use(...editorRoutes());

		renderPage({});

		expect(await screen.findByTestId("gw-definition-list")).toBeDefined();
		expect(screen.queryByTestId("graph-workflow-run-toolbar")).toBeNull();
	});

	it("shows the run view — toolbar and node-run table — as soon as a runId is in the selection", async () => {
		server.use(...runViewRoutes());

		renderPage({ definitionId, runId, tab: "runs" });

		expect(await screen.findByTestId("graph-workflow-run-toolbar")).toBeDefined();
		expect(await screen.findByTestId("graph-workflow-node-run-table")).toBeDefined();
		// The editor is not merely hidden behind a tab: one search param picks the whole mode.
		expect(screen.queryByTestId("gw-definition-list")).toBeNull();
	});

	it("opens a definition through the selection rather than through page state", async () => {
		server.use(...editorRoutes());
		const { onSelectionChange } = renderPage({});

		fireEvent.click(await screen.findByTestId(`gw-definition-open-${definitionId}`));

		// Exactly this, with no run and no node carried over: picking a workflow opens ITS editor.
		expect(onSelectionChange).toHaveBeenCalledWith({ definitionId });
	});

	it("loads the definition into the editor clean, and only enables Save once the canvas differs", async () => {
		server.use(...editorRoutes());

		renderPage({ definitionId });

		const save = await screen.findByTestId<HTMLButtonElement>("gw-page-save");
		// A definition that was saved by this editor reads clean on reopen — nothing to save yet.
		expect(save.disabled).toBe(true);

		await openAndDirty();
	});

	it("keeps Save disabled while the graph has a structural problem, however dirty the canvas is", async () => {
		// No End node, and no stored positions. The missing positions are what make it dirty on open (ruling C4), so
		// Save being disabled here can only be the structural rule — not "nothing has changed".
		const noEndGraph: GraphWorkflowGraph = {
			schemaVersion: 1,
			nodes: [
				{ key: "start", kind: "Start", label: "Start", config: { inputSchema: null, defaultInput: null } },
				{ key: "work", kind: "Agent", label: "Work", config: {} },
			],
			edges: [{ key: "e1", from: "start", to: "work" }],
		};
		server.use(...editorRoutes(noEndGraph));

		renderPage({ definitionId });

		// The laid-out canvas is unsaved work, presented as a normal state rather than as a failure.
		expect(await screen.findByTestId("gw-page-unsaved-layout")).toBeDefined();
		expect(screen.getByTestId<HTMLButtonElement>("gw-page-save").disabled).toBe(true);
		expect(screen.getByTestId("graph-workflow-validation-unkeyed").textContent).toMatch(/no End node/i);
	});

	it("refuses to start a run from a dirty canvas and says why", async () => {
		server.use(...editorRoutes());

		renderPage({ definitionId });
		await openAndDirty();

		expect(screen.getByTestId<HTMLButtonElement>("gw-page-start-run").disabled).toBe(true);
		expect(screen.getByTestId("gw-page-save-first").textContent).toMatch(/Save first/i);
	});

	it("renders the saved-elsewhere alert when the save loses the version race", async () => {
		server.use(
			...editorRoutes(),
			validateRoute({ valid: true }),
			http.put(localApiPath(`graph-workflows/definitions/${definitionId}`), () =>
				HttpResponse.json(
					{
						type: "about:blank",
						title: "Conflict",
						status: 409,
						detail: "The definition version does not match.",
						conflictType: "GraphWorkflowDefinitionConflict",
					},
					{ status: 409, headers: { "content-type": "application/problem+json" } },
				),
			),
		);

		renderPage({ definitionId });
		await openAndDirty();
		fireEvent.click(screen.getByTestId("gw-page-save"));

		expect(await screen.findByTestId("gw-page-save-conflict")).toBeDefined();
		expect(screen.getByTestId("gw-page-reload")).toBeDefined();
	});

	it("attaches a server validation error to the node it names and never writes the definition", async () => {
		const put = vi.fn();
		server.use(
			...editorRoutes(),
			validateRoute({ valid: false, errors: [{ key: "review", message: "This Pause offers a decision no edge routes." }] }),
			http.put(localApiPath(`graph-workflows/definitions/${definitionId}`), () => {
				put();
				return HttpResponse.json(graphWorkflowDefinition({ version: 2 }));
			}),
		);

		renderPage({ definitionId });
		await openAndDirty();
		fireEvent.click(screen.getByTestId("gw-page-save"));

		const issue = await screen.findByTestId("graph-workflow-validation-issue-review");
		expect(issue.textContent).toBe("This Pause offers a decision no edge routes.");
		// A refused graph costs no version bump: the write is not attempted at all.
		expect(put).not.toHaveBeenCalled();
	});

	it("offers a Pause node exactly the decisions its definition allows, with the definition's prompt", async () => {
		// Approve ONLY — the decision panel's own fallback is ["Approve", "Reject"], so a Reject button here would mean
		// the page never passed the Pause config down.
		const approveOnly: GraphWorkflowGraph = {
			...eightNodeGraph,
			nodes: (eightNodeGraph.nodes ?? []).map((node) =>
				node.key === "review"
					? { ...node, config: { prompt: "Approve the analysis?", allowedDecisions: ["Approve"], requireComment: false } }
					: node,
			),
		};
		server.use(
			...runViewRoutes(approveOnly),
			jsonRoute("get", `graph-workflows/runs/${runId}/nodes/review`, pendingPauseNodeRun()),
		);

		renderPage({ definitionId, runId, nodeKey: "review", tab: "runs" });

		expect(await screen.findByTestId("graph-workflow-decision-Approve")).toBeDefined();
		expect(screen.queryByTestId("graph-workflow-decision-Reject")).toBeNull();
		expect(screen.getByTestId("graph-workflow-decision-prompt").textContent).toBe("Approve the analysis?");
	});
});

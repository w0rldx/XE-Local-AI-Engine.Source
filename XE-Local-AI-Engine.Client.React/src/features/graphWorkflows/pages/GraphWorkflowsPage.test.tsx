// @vitest-environment jsdom

// The page is composition, so what is pinned here is the WIRING nothing else can hold: which mode one search param
// picks, that a definition load reaches the editor's dirty check, that Save is gated on both dirtiness and the
// server's answer, and that a Pause node's decision buttons come from the DEFINITION rather than from the panel's
// fallback. Every component below is real; only the three things jsdom cannot host are stood in for.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
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
	// One button per node, so a card click is reachable: selecting a node is a search-param write, and that write is
	// what the navigation guard used to block.
	ReactFlow: ({
		nodes,
		onNodeClick,
	}: {
		readonly nodes?: readonly { readonly id: string }[];
		readonly onNodeClick?: (event: unknown, node: { readonly id: string }) => void;
	}) => (
		<div data-testid="react-flow">
			{(nodes ?? []).map((node) => (
				<button key={node.id} type="button" data-testid={`flow-node-${node.id}`} onClick={(event) => onNodeClick?.(event, node)}>
					{node.id}
				</button>
			))}
		</div>
	),
}));

// Monaco is ~3 MB behind a lazy import and wants a layout engine. The documents are other files' subject.
vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({
	CodeEditor: ({ value, "data-testid": testId }: { value: string; "data-testid"?: string }) => (
		<textarea data-testid={testId} readOnly={true} value={value} />
	),
}));

// `useUnsavedChangesGuard` reaches for router context; the confirm-driving half is its own file's subject. Here the
// blocker is inert (so the page renders without the async memory-router mount) but RECORDED, because the predicate the
// page hands it is the whole of "does selecting a node count as leaving the editor".
interface BlockerOpts {
	readonly shouldBlockFn: (args: { current: { pathname: string }; next: { pathname: string } }) => boolean;
}

const blockerMock = vi.hoisted(() =>
	vi.fn((_options: BlockerOpts) => ({ status: "idle", proceed: undefined, reset: undefined })),
);

vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useBlocker: blockerMock,
}));

/** The predicate the page most recently gave the router, asked about a move that keeps the pathname. */
function blocksSameRouteMove(): boolean {
	const options = blockerMock.mock.calls.at(-1)?.[0];
	if (options === undefined) {
		throw new Error("the page never armed the navigation guard");
	}
	return options.shouldBlockFn({ current: { pathname: "/graph-workflows" }, next: { pathname: "/graph-workflows" } });
}

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

/**
 * The run and the definition it was started from. `graph` is BOTH the run's pinned graph and the definition's current
 * one, which is the ordinary case; `definitionNow` overrides only the definition, standing in for an edit made after
 * the run started. The run response always carries a graph, so there is no "no pinned graph" variant here — the canvas
 * model's own tests cover that notice.
 */
function runViewRoutes(
	graph: GraphWorkflowGraph = eightNodeGraph,
	options: { readonly definitionNow?: GraphWorkflowGraph } = {},
) {
	return [
		jsonRoute("get", `graph-workflows/runs/${runId}`, graphWorkflowRun({ graph })),
		jsonRoute("get", "graph-workflows/runs", { runs: [graphWorkflowRunSummary()] }),
		jsonRoute(
			"get",
			`graph-workflows/definitions/${definitionId}`,
			graphWorkflowDefinition(
				options.definitionNow === undefined
					? { graph }
					: { graph: options.definitionNow, graphHash: "sha256:someone-saved-since" },
			),
		),
	];
}

/**
 * The server's structural check. `valid: false` is what puts an error on a node, so both answers are needed, and
 * `warnings` is the second, non-blocking list: `valid` stays `errors.length === 0` whatever is in it.
 */
function validateRoute(body: {
	valid: boolean;
	errors?: { key: string | null; message: string }[];
	warnings?: { key: string | null; message: string }[];
}) {
	return http.post(localApiPath("graph-workflows/definitions/validate"), () =>
		HttpResponse.json({ valid: body.valid, errors: body.errors ?? [], warnings: body.warnings ?? [], nodeCount: 8 }),
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

	// Live round, HIGH: clicking a card with unsaved edits opened "Discard unsaved changes?", because selecting a node
	// writes `nodeKey` through the router and the guard blocked every transition. Configuring the node you just added
	// must not cost you the graph.
	it("lets a dirty editor select a node without treating it as leaving the editor", async () => {
		server.use(...editorRoutes());
		const { onSelectionChange } = renderPage({ definitionId });
		await openAndDirty();

		fireEvent.click(screen.getByTestId("flow-node-analyze"));

		expect(onSelectionChange).toHaveBeenCalledWith({ definitionId, nodeKey: "analyze" });
		// Still dirty, and still guarded against a real departure — only the same-route move is let through.
		expect(screen.getByTestId<HTMLButtonElement>("gw-page-save").disabled).toBe(false);
		expect(blocksSameRouteMove()).toBe(false);
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

	it("offers a Pause node exactly the decisions the run's graph allows, with that graph's prompt", async () => {
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
	it("reads the run's PINNED graph, not the edited definition, and only says the definition moved on", async () => {
		// The Pause was renamed and re-prompted in the definition since. jsdom measures React Flow at 0x0 so no card
		// paints here (the view's own suite mocks it); the decision panel is the observable that names WHICH graph the
		// page read, and it must be the one the run pinned.
		const definitionNow: GraphWorkflowGraph = {
			...eightNodeGraph,
			nodes: (eightNodeGraph.nodes ?? []).map((node) =>
				node.key === "review"
					? { ...node, config: { prompt: "Sign this off?", allowedDecisions: ["Approve"], requireComment: false } }
					: node,
			),
		};
		server.use(
			...runViewRoutes(eightNodeGraph, { definitionNow }),
			jsonRoute("get", `graph-workflows/runs/${runId}/nodes/review`, pendingPauseNodeRun()),
		);

		renderPage({ definitionId, runId, nodeKey: "review", tab: "runs" });

		expect((await screen.findByTestId("graph-workflow-decision-prompt")).textContent).toBe("Approve the analysis?");
		// The pinned graph allows both; the edited definition allows only Approve, so a Reject button proves the source.
		expect(screen.getByTestId("graph-workflow-decision-Reject")).toBeDefined();
		// Informational, not a degradation: the banner never says the connections are unknown.
		const banner = screen.getByTestId("graph-workflow-run-graph-mismatch");
		expect(banner.textContent).toContain("the graph the run itself ran on");
		expect(banner.textContent).not.toContain("nodes only");
	});

	it("says nothing about the graph when the definition still is the one the run ran on", async () => {
		server.use(...runViewRoutes());

		renderPage({ definitionId, runId, tab: "runs" });

		expect(await screen.findByTestId("graph-workflow-node-run-table")).toBeDefined();
		expect(screen.queryByTestId("graph-workflow-run-graph-mismatch")).toBeNull();
	});

	it("shows a server warning without blocking the save, and keeps it through the save's own reload", async () => {
		const put = vi.fn();
		// The write bumps the version, and the GET answers the NEW one from then on — which is what makes the
		// definition query refetch and the editor reload. Answering version 1 forever hid the bug this pins: the
		// reload mints a fresh graph object, and pinning the server's answer by reference erased the warning.
		let version = 1;
		let stored: GraphWorkflowGraph = eightNodeGraph;
		server.use(
			jsonRoute("get", "graph-workflows/definitions", { definitions: [graphWorkflowDefinitionSummary()] }),
			http.get(localApiPath(`graph-workflows/definitions/${definitionId}`), () =>
				HttpResponse.json(graphWorkflowDefinition({ version, graph: stored })),
			),
			validateRoute({
				valid: true,
				warnings: [{ key: "done", message: "'done' is reached only through the Pause node 'review'." }],
			}),
			// Stores what it was sent, the way the real endpoint does: the reload has to answer the graph that was
			// SAVED, or the editor reloads a different document and the warning is right to disappear.
			http.put(localApiPath(`graph-workflows/definitions/${definitionId}`), async ({ request }) => {
				put();
				stored = ((await request.json()) as { graph: GraphWorkflowGraph }).graph;
				version = 2;
				return HttpResponse.json(graphWorkflowDefinition({ version: 2, graph: stored }));
			}),
		);

		renderPage({ definitionId });
		await openAndDirty();
		fireEvent.click(screen.getByTestId("gw-page-save"));

		const warnings = await screen.findByTestId("graph-workflow-validation-warnings");
		expect(warnings.textContent).toContain("reached only through the Pause node");
		// Non-blocking on both counts: the write went through, and nothing joined the red half.
		await waitFor(() => {
			expect(put).toHaveBeenCalled();
		});
		// The reload lands here: version 2, a fresh graph object for the same document, and Save clean again.
		await waitFor(() => {
			expect(screen.getByTestId<HTMLButtonElement>("gw-page-save").disabled).toBe(true);
		});
		expect(screen.getByTestId("graph-workflow-validation-warnings").textContent).toContain("reached only through the Pause node");
		expect(screen.queryByTestId("graph-workflow-validation-unkeyed")).toBeNull();
		expect(screen.queryByTestId("graph-workflow-validation-issues")).toBeNull();
	});

	it("clears a warning the same way it clears an error — on the next edit", async () => {
		server.use(
			...editorRoutes(),
			validateRoute({
				valid: true,
				warnings: [{ key: "done", message: "'done' is reached only through the Pause node 'review'." }],
			}),
		);

		renderPage({ definitionId });
		// Validated on the graph as STORED, so the auto-arrange below is a real edit rather than a repeat of one.
		await waitFor(() => {
			expect(screen.getByTestId("gw-page-definition-name").textContent).toBe("Analyze → review → read");
		});
		fireEvent.click(screen.getByTestId("gw-page-validate"));
		expect(await screen.findByTestId("graph-workflow-validation-warnings")).toBeDefined();

		// The server's answer is about the graph it was asked about; moving every node makes it a different graph.
		fireEvent.click(screen.getByTestId("graph-workflow-auto-arrange"));

		await waitFor(() => {
			expect(screen.queryByTestId("graph-workflow-validation-warnings")).toBeNull();
		});
	});
	it("selects nothing when a server issue names a key the canvas no longer holds", async () => {
		// A stale key used to fall through to `selectEdge`, which — with a node open — cleared that node's selection
		// and closed the panel the operator was working in, to select an edge that does not exist.
		server.use(
			...editorRoutes(),
			validateRoute({ valid: false, errors: [{ key: "deleted-node", message: "This node is gone." }] }),
		);

		const { onSelectionChange } = renderPage({ definitionId, nodeKey: "analyze" });
		await openAndDirty();
		fireEvent.click(screen.getByTestId("gw-page-save"));
		const chip = await screen.findByTestId("graph-workflow-validation-issue-deleted-node");
		onSelectionChange.mockClear();

		fireEvent.click(chip);

		expect(onSelectionChange).not.toHaveBeenCalled();
	});
});

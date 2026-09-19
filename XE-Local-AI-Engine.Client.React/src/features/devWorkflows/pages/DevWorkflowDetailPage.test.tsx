// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { HttpResponse, http } from "msw";
import { useState } from "react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { DevWorkflowDetailPage, type DevWorkflowDetailSelection } from "@/features/devWorkflows/pages/DevWorkflowDetailPage";
import {
	devWorkflowNodeRunDetail,
	devWorkflowNodeRunSummary,
	devWorkflowRun,
	devWorkflowRunSummary,
	devWorkflowTestIds,
	devWorkflowWorkItem,
} from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { jsonRoute, localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const navigate = vi.hoisted(() => vi.fn());

vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigate,
}));

// The hub is exercised on its own in useDevWorkflowRunHub.test.tsx; here it only has to not reach for a real socket.
// Hoisted into a named spy rather than an inline arrow so one test can assert the hub is never ACQUIRED on a node
// with the feature switched off; every call still answers a fresh connection object, exactly as before.
const hubMock = vi.hoisted(() => ({
	acquire: vi.fn(() => ({
		connection: { state: "Disconnected", on: vi.fn(), off: vi.fn(), invoke: vi.fn() },
		whenStarted: Promise.resolve(),
		onReconnected: () => vi.fn(),
		onReconnecting: () => vi.fn(),
		onClosed: () => vi.fn(),
		release: vi.fn(),
	})),
}));

vi.mock("@/core/api/signalr/SharedHubConnection", () => ({ acquireHubConnection: hubMock.acquire }));

const { workItem: workItemId, run: runId, nodeRun: nodeRunId } = devWorkflowTestIds;
/** An older run of the same work item — the one an operator selects with `?run=` to read a finished attempt. */
const olderRunId = "77777777-7777-4777-8777-777777777777";

/** The feeds a selected run pulls, for any run id: the detail page reads all three off whatever `?run=` names. */
function runRoutes(id: string, status: string) {
	return [
		jsonRoute("get", `development-workflows/runs/${id}`, devWorkflowRun({ id, status, nodes: [] })),
		jsonRoute("get", `development-workflows/runs/${id}/events`, { items: [], lastSequence: 0, hasMore: false }),
		jsonRoute("get", `development-workflows/runs/${id}/artifacts`, { items: [], lastSequence: 0 }),
	];
}

const startableDefinition = {
	id: devWorkflowTestIds.definition,
	name: "Research → Plan → Approval",
	version: 1,
	nodeCount: 3,
	archived: false,
};

function baseRoutes(overrides: { run?: unknown; nodeRun?: unknown } = {}) {
	return [
		jsonRoute("get", `development-workflows/work-items/${workItemId}`, devWorkflowWorkItem()),
		jsonRoute("get", `development-workflows/runs/${runId}`, overrides.run ?? devWorkflowRun()),
		jsonRoute("get", `development-workflows/runs/${runId}/events`, { items: [], lastSequence: 0, hasMore: false }),
		jsonRoute("get", `development-workflows/runs/${runId}/artifacts`, { items: [], lastSequence: 0 }),
		jsonRoute("get", "development-workflows/definitions", { items: [] }),
		jsonRoute("get", `development-workflows/runs/${runId}/nodes/${nodeRunId}`, overrides.nodeRun ?? devWorkflowNodeRunDetail()),
	];
}

function setViewportWidth(width: number): void {
	Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: width });
}

/**
 * jsdom gives every element a `clientWidth` of 0, and `usePaneLayoutMode` measures in a layout effect — before a test
 * could get a handle on the node — so the stub goes on the prototype. Removed again in `afterEach`.
 */
function stubContainerWidth(width: number): void {
	Object.defineProperty(HTMLElement.prototype, "clientWidth", { configurable: true, get: () => width });
}

function renderPage(selection: DevWorkflowDetailSelection = {}) {
	const onSelectionChange = vi.fn();
	renderWithProviders(
		<ConfirmProvider>
			<DevWorkflowDetailPage workItemId={workItemId} selection={selection} onSelectionChange={onSelectionChange} />
		</ConfirmProvider>,
	);
	return { onSelectionChange };
}

/** Edit lives behind the header's action menu, next to Delete — the one place a work item is acted on as a whole. */
async function openEditDialog(): Promise<void> {
	fireEvent.click(await screen.findByTestId("dev-workflow-actions"));
	fireEvent.click(await screen.findByTestId("dev-workflow-edit"));
	await screen.findByTestId("edit-dev-workflow-work-item-dialog");
}

/**
 * The node's DevWorkflows:Enabled switch, which this page reads before anything else. It ships OFF, so every test
 * that wants the real detail view has to say the node has it on.
 */
function capabilityRoute(enabled = true) {
	return jsonRoute("get", "development-workflows/capability", { enabled });
}

setupMswServer(capabilityRoute());

describe("DevWorkflowDetailPage", () => {
	beforeEach(() => {
		// jsdom's default, and TWO_PANE_BREAKPOINT itself: the desktop layout is what every other test here assumes.
		setViewportWidth(1024);
		navigate.mockClear();
		hubMock.acquire.mockClear();
	});

	afterEach(() => {
		cleanup();
		// A no-op when the test never stubbed it; jsdom's own prototype getter comes back either way.
		Reflect.deleteProperty(HTMLElement.prototype, "clientWidth");
	});

	it("resolves the latest run when no run is selected and renders its node-run table", async () => {
		server.use(...baseRoutes());
		renderPage();

		expect(await screen.findByTestId("dev-workflow-title")).toBeDefined();
		expect(await screen.findByTestId(`dev-workflow-node-row-${nodeRunId}`)).toBeDefined();
		expect(screen.getByTestId("dev-workflow-run-toolbar")).toBeDefined();
	});

	// TWO_PANE_BREAKPOINT answers "do two panes fit at all", not "does 320 + 380 plus this page's chrome fit", so
	// just above it the unfloored centre track was squeezed under its own tab header and clipped it. The floor is the
	// fix; the horizontal scroller is what keeps the overflow it can now produce visible, because FullHeightPage
	// deliberately clips the X axis.
	// Heading navigation has to land somewhere on every state of this page: the work item's own title once it is
	// loaded, and the navigation label while there is no name to show. One h1 either way, never two.
	it("carries exactly one h1 while the work item loads and once it has", async () => {
		server.use(...baseRoutes());
		renderPage();

		// Read synchronously, before the work-item query can settle: this is the pending tree, no waiting involved.
		const pending = screen.getAllByRole("heading", { level: 1 });
		expect(pending).toHaveLength(1);
		expect(pending[0]?.textContent).toBe("Workflow Runs");

		// Waited for by test id, not by role: the pending h1 above would satisfy a role query straight away.
		await screen.findByTestId("dev-workflow-title");
		const loaded = screen.getAllByRole("heading", { level: 1 });
		expect(loaded).toHaveLength(1);
		expect(loaded[0]?.textContent).toBe("Survey the vector-store options");
	});

	it("floors the centre column and scrolls sideways rather than clipping it", async () => {
		server.use(...baseRoutes());
		renderPage();

		const grid = await screen.findByTestId("dev-workflow-detail-grid");

		expect(grid.style.gridTemplateColumns).toBe("320px minmax(240px, 1fr) minmax(380px, 420px)");
		expect(grid.style.overflowX).toBe("auto");
	});

	// The other side of the same switch: below the breakpoint the grid is gone entirely and BOTH side surfaces are
	// reachable only through the header's toggles, so a missing toggle strands the operator on the centre pane with no
	// way back to the run list. Mirrors the work-session detail page, which collapses through the identical mode.
	it("collapses to the centre pane with two drawers below 1024px", async () => {
		setViewportWidth(800);
		server.use(...baseRoutes());
		renderPage();

		await screen.findByTestId("dev-workflow-centre-pane");
		expect(screen.queryByTestId("dev-workflow-detail-grid")).toBeNull();
		expect(screen.queryByTestId("dev-workflow-run-summary-panel")).toBeNull();
		expect(screen.queryByTestId("dev-workflow-side-tabs")).toBeNull();

		fireEvent.click(screen.getByTestId("dev-workflow-summary-toggle"));
		expect(await screen.findByTestId("dev-workflow-run-summary-panel")).toBeDefined();

		fireEvent.click(screen.getByTestId("dev-workflow-side-toggle"));
		expect(await screen.findByTestId("dev-workflow-side-tabs")).toBeDefined();
	});

	// The grid lives inside the app shell, so a wide WINDOW is not a wide container: the sidebar and the content
	// padding take ~250px before the panes see any of it, and the sidebar collapses with no resize event at all.
	// The toggles and the grid now read one decision, so a wide viewport can no longer leave the third column
	// reachable only by scrolling sideways while the header claims there is nothing to open.
	it("collapses on a wide viewport when the container is too narrow for three columns", async () => {
		setViewportWidth(1600);
		stubContainerWidth(725);
		server.use(...baseRoutes());
		renderPage();

		await screen.findByTestId("dev-workflow-centre-pane");
		expect(screen.queryByTestId("dev-workflow-detail-grid")).toBeNull();
		expect(screen.getByTestId("dev-workflow-summary-toggle")).toBeDefined();
		expect(screen.getByTestId("dev-workflow-side-toggle")).toBeDefined();
	});

	it("shows the artifacts and events tabs when no node is selected", async () => {
		server.use(...baseRoutes());
		renderPage();

		expect(await screen.findByTestId("dev-workflow-side-tabs")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-node-panel")).toBeNull();
	});

	it("opens the node pane for ?node= and drops the tabs, so the drill-down is a shareable URL", async () => {
		server.use(...baseRoutes());
		renderPage({ node: nodeRunId });

		expect(await screen.findByTestId("dev-workflow-node-panel")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-side-tabs")).toBeNull();
	});

	it("selects a node-run from the table through the search params rather than a route push", async () => {
		server.use(...baseRoutes());
		const { onSelectionChange } = renderPage();

		fireEvent.click(await screen.findByTestId(`dev-workflow-node-row-${nodeRunId}`));

		expect(onSelectionChange).toHaveBeenCalledWith({ node: nodeRunId });
		expect(navigate).not.toHaveBeenCalled();
	});

	it("jumps from the toolbar's decision count straight to the node that is blocking the run", async () => {
		server.use(
			...baseRoutes({
				run: devWorkflowRun({
					status: "WaitingForApproval",
					pendingDecisionCount: 1,
					blockingGateNodeRunId: nodeRunId,
					nodes: [devWorkflowNodeRunSummary({ status: "WaitingForApproval", pendingDecisionKind: "Approve" })],
				}),
			}),
		);
		const { onSelectionChange } = renderPage();

		const jump = await screen.findByTestId("dev-workflow-decisions-needed");
		// The count comes from pendingDecisionCount, never from the run badge: the run reads WaitingForApproval for an
		// open gate AND for a node needing intervention.
		expect(jump.textContent).toBe("1 decision needed");
		fireEvent.click(jump);

		expect(onSelectionChange).toHaveBeenCalledWith({ node: nodeRunId });
	});

	it("posts a gate decision with the `decision` field and the panel's operation id", async () => {
		const bodies: unknown[] = [];
		server.use(
			...baseRoutes({
				nodeRun: devWorkflowNodeRunDetail({
					nodeType: "HumanGate",
					status: "WaitingForApproval",
					pendingDecisionKind: "Approve",
					allowedDecisions: ["Approve", "Reject"],
					hasRejectBranch: true,
				}),
			}),
			http.post(localApiPath(`development-workflows/runs/${runId}/nodes/${nodeRunId}/decision`), async ({ request }) => {
				bodies.push(await request.json());
				return HttpResponse.json({
					decision: { id: "d1", nodeRunId, attempt: 1, decision: "Approve", sequence: 30 },
					runStatus: "Running",
					nodeRunStatus: "Succeeded",
				});
			}),
		);
		renderPage({ node: nodeRunId });

		fireEvent.click(await screen.findByTestId("dev-workflow-gate-Approve"));

		await waitFor(() => expect(bodies).toHaveLength(1));
		const body = bodies[0] as { decision: string; kind?: string; operationId: string };
		// The field was renamed from `kind`; sending the old name would be accepted as an empty decision.
		expect(body.decision).toBe("Approve");
		expect(body.kind).toBeUndefined();
		expect(body.operationId).toMatch(/^[0-9a-f-]{36}$/i);
	});

	it("re-reads the node after a 409, so a settled gate cannot keep its live buttons", async () => {
		let nodeReads = 0;
		const gateNode = devWorkflowNodeRunDetail({
			nodeType: "HumanGate",
			status: "WaitingForApproval",
			pendingDecisionKind: "Approve",
			allowedDecisions: ["Approve"],
		});
		server.use(
			// Registered BEFORE baseRoutes so this counting handler wins the match for the node-run route.
			http.get(localApiPath(`development-workflows/runs/${runId}/nodes/${nodeRunId}`), () => {
				nodeReads += 1;
				return HttpResponse.json(gateNode);
			}),
			...baseRoutes(),
			http.post(localApiPath(`development-workflows/runs/${runId}/nodes/${nodeRunId}/decision`), () =>
				HttpResponse.json(
					{
						type: "about:blank",
						title: "Conflict",
						status: 409,
						detail: "already answered",
						conflictType: "DevWorkflowGateAlreadyDecided",
						standingDecision: "Approve",
					},
					{ status: 409, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		renderPage({ node: nodeRunId });

		fireEvent.click(await screen.findByTestId("dev-workflow-gate-Approve"));

		// The refusal names the standing decision, AND the node is re-read: without the refetch the panel would keep
		// offering a decision on a gate the server has already settled, and every further click would 409 again.
		expect(await screen.findByTestId("dev-workflow-gate-already-decided")).toBeDefined();
		const readsAfterFailure = nodeReads;
		await waitFor(() => expect(nodeReads).toBeGreaterThan(1));
		expect(readsAfterFailure).toBeGreaterThan(0);
	});

	it("offers a run to start, and no run to cancel, for a work item that has never run", async () => {
		server.use(
			jsonRoute("get", `development-workflows/work-items/${workItemId}`, devWorkflowWorkItem({ latestRunId: null, runs: [] })),
			jsonRoute("get", "development-workflows/definitions", { items: [startableDefinition] }),
		);
		renderPage();

		expect(await screen.findByTestId("dev-workflow-detail-no-run")).toBeDefined();
		expect(screen.getByTestId("dev-workflow-start-run")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-run-toolbar")).toBeNull();
	});

	it("offers no Start while a newer run is live, even though the SELECTED run has finished", async () => {
		let definitionReads = 0;
		server.use(
			// Registered first so this counting handler wins the match, and the assertion below can wait for the picker's
			// own feed rather than for a timeout: a Start hidden because the definitions never arrived proves nothing.
			http.get(localApiPath("development-workflows/definitions"), () => {
				definitionReads += 1;
				return HttpResponse.json({ items: [startableDefinition] });
			}),
			jsonRoute(
				"get",
				`development-workflows/work-items/${workItemId}`,
				devWorkflowWorkItem({
					latestRunId: runId,
					runs: [devWorkflowRunSummary({ id: olderRunId, status: "Completed" }), devWorkflowRunSummary({ status: "Running" })],
				}),
			),
			...runRoutes(olderRunId, "Completed"),
		);
		renderPage({ run: olderRunId });

		expect(await screen.findByTestId(`dev-workflow-run-${runId}`)).toBeDefined();
		await waitFor(() => expect(definitionReads).toBeGreaterThan(0));
		// The server allows one live run per work item: offering the control here would earn a 409 on every click.
		expect(screen.queryByTestId("dev-workflow-start-run")).toBeNull();
	});

	it("offers a Start once every run is terminal, whichever run is selected", async () => {
		server.use(
			jsonRoute(
				"get",
				`development-workflows/work-items/${workItemId}`,
				devWorkflowWorkItem({
					latestRunId: runId,
					runs: [devWorkflowRunSummary({ id: olderRunId, status: "Cancelled" }), devWorkflowRunSummary({ status: "Completed" })],
				}),
			),
			jsonRoute("get", "development-workflows/definitions", { items: [startableDefinition] }),
			...runRoutes(olderRunId, "Cancelled"),
		);
		renderPage({ run: olderRunId });

		expect(await screen.findByTestId("dev-workflow-start-run")).toBeDefined();
	});

	it("degrades to a polling notice rather than an error when the hub cannot subscribe", async () => {
		server.use(...baseRoutes());
		renderPage();

		// The mocked connection never reaches Connected, so the hook stays out of "connected" and the page keeps
		// rendering last-good state. The notice is the toolbar's, and it must not be an error.
		await screen.findByTestId("dev-workflow-run-toolbar");
		expect(screen.queryByTestId("dev-workflow-detail-error")).toBeNull();
	});

	// A1: the centre pane is two views over one selection. `?tab=` is one param across both tab strips — the centre
	// defaults to `graph`, the side pane to `artifacts` — so a value belonging to the other strip reads as that
	// strip's default rather than blanking it.
	it("opens on the graph with no tab selected, and keeps the node table mounted behind it", async () => {
		server.use(...baseRoutes());
		renderPage();

		expect((await screen.findByTestId("dev-workflow-tab-graph")).getAttribute("aria-selected")).toBe("true");
		expect(screen.getByTestId("dev-workflow-tab-nodes").getAttribute("aria-selected")).toBe("false");
		// Mantine keeps an inactive panel mounted, which is the point: the table is still the keyboard path through the
		// run, and every A0 test that finds a row without naming a tab still finds one.
		expect(await screen.findByTestId(`dev-workflow-node-row-${nodeRunId}`)).toBeDefined();
	});

	it("activates the node table for ?tab=nodes and leaves the side tabs on artifacts", async () => {
		server.use(...baseRoutes());
		renderPage({ tab: "nodes" });

		expect((await screen.findByTestId("dev-workflow-tab-nodes")).getAttribute("aria-selected")).toBe("true");
		expect(screen.getByTestId("dev-workflow-tab-artifacts").getAttribute("aria-selected")).toBe("true");
	});

	it("selects the centre tab through the search params, like every other selection on this page", async () => {
		server.use(...baseRoutes());
		const { onSelectionChange } = renderPage();

		fireEvent.click(await screen.findByTestId("dev-workflow-tab-nodes"));

		expect(onSelectionChange).toHaveBeenCalledWith({ tab: "nodes" });
		expect(navigate).not.toHaveBeenCalled();
	});

	it("leaves the centre pane alone when a side tab is clicked, because both strips write the same param", async () => {
		server.use(...baseRoutes());
		// The selection has to round-trip for this to mean anything: the side tab overwrites `tab` with `events`, and
		// the centre pane must not read its own state back out of it.
		function StatefulDetailPage() {
			const [selection, setSelection] = useState<DevWorkflowDetailSelection>({ tab: "nodes" });
			return (
				<DevWorkflowDetailPage
					workItemId={workItemId}
					selection={selection}
					onSelectionChange={(next) => setSelection((current) => ({ ...current, ...next }))}
				/>
			);
		}
		renderWithProviders(
			<ConfirmProvider>
				<StatefulDetailPage />
			</ConfirmProvider>,
		);

		fireEvent.click(await screen.findByTestId("dev-workflow-tab-events"));

		expect(screen.getByTestId("dev-workflow-tab-events").getAttribute("aria-selected")).toBe("true");
		expect(screen.getByTestId("dev-workflow-tab-nodes").getAttribute("aria-selected")).toBe("true");
	});
	// The route is reachable by bookmark on a node that has the feature off, where the work-item GET is a bodyless
	// 404 — which this page rendered as "This work item could not be loaded", the same words a genuinely missing id
	// gets. The capability read is what tells them apart.
	it("says the feature is switched off on this node instead of reporting a missing work item", async () => {
		let workItemReads = 0;
		server.use(
			capabilityRoute(false),
			http.get(localApiPath(`development-workflows/work-items/${workItemId}`), () => {
				workItemReads += 1;
				return new HttpResponse(null, { status: 404 });
			}),
		);
		renderPage();

		const disabled = await screen.findByTestId("dev-workflows-disabled");
		expect(disabled.textContent).toBe("Development workflows are disabled by this node's runtime configuration.");
		expect(screen.queryByTestId("dev-workflow-detail-error")).toBeNull();
		// Gated, not merely hidden: a disabled node must not be asked for data it will refuse.
		await waitFor(() => expect(workItemReads).toBe(0));
	});

	// A `?run=` in the URL names a run id without the work item having loaded, so the hub subscribe is a second path
	// onto a disabled node. It must stay idle too.
	it("does not subscribe the run hub while the feature is switched off", async () => {
		server.use(capabilityRoute(false));
		renderPage({ run: runId });

		await screen.findByTestId("dev-workflows-disabled");
		expect(hubMock.acquire).not.toHaveBeenCalled();
	});

	// A capability call that FAILED says nothing about the switch, so reporting it as "switched off" would be a guess.
	it("reports a failed capability check as an error, not as a switched-off feature", async () => {
		server.use(problemDetailsRoute("get", "development-workflows/capability", 500, { detail: "the node is unreachable" }));
		renderPage();

		const alert = await screen.findByTestId("dev-workflows-disabled");
		expect(alert.textContent).toContain("the node is unreachable");
		expect(alert.textContent).not.toContain("disabled by this node's runtime configuration");
	});

	// The PATCH accepts title and request and nothing else, and refuses in no run state — the runtime's only write to a
	// work item is its STATUS — so what these owe is the payload contract, not a matrix of states the server never rejects.
	it("opens the edit dialog on the loaded work item and keeps Save unavailable until something changes", async () => {
		server.use(...baseRoutes());
		renderPage();
		await openEditDialog();

		expect((screen.getByTestId("edit-dev-workflow-work-item-title") as HTMLInputElement).value).toBe(
			"Survey the vector-store options",
		);
		expect((screen.getByTestId("edit-dev-workflow-work-item-request") as HTMLTextAreaElement).value).toBe(
			"Compare the options and propose one.",
		);
		const submit = screen.getByTestId("edit-dev-workflow-work-item-submit");
		expect(submit).toHaveProperty("disabled", true);

		fireEvent.change(screen.getByTestId("edit-dev-workflow-work-item-title"), { target: { value: "Survey the vector stores" } });
		expect(submit).toHaveProperty("disabled", false);

		// Mirrors the server's validator: a member that is PRESENT must not be blank, so an emptied field is not a save.
		fireEvent.change(screen.getByTestId("edit-dev-workflow-work-item-request"), { target: { value: "  " } });
		expect(submit).toHaveProperty("disabled", true);
	});

	it("patches only the field that changed, then shows the new title once the work item is re-read", async () => {
		let sentBody: Record<string, unknown> | undefined;
		let title = "Survey the vector-store options";
		server.use(
			// Ahead of `baseRoutes()`, whose own work-item route would otherwise win and answer the pre-edit title forever.
			http.get(localApiPath(`development-workflows/work-items/${workItemId}`), () =>
				HttpResponse.json(devWorkflowWorkItem({ title })),
			),
			...baseRoutes(),
			http.patch(localApiPath(`development-workflows/work-items/${workItemId}`), async ({ request }) => {
				sentBody = (await request.json()) as Record<string, unknown>;
				title = "Survey the vector stores";
				return HttpResponse.json(devWorkflowWorkItem({ title, version: 2 }));
			}),
		);
		renderPage();
		await openEditDialog();

		fireEvent.change(screen.getByTestId("edit-dev-workflow-work-item-title"), { target: { value: "Survey the vector stores" } });
		fireEvent.click(screen.getByTestId("edit-dev-workflow-work-item-submit"));

		// The untouched request is ABSENT, not echoed back: the endpoint reads an omitted member as "leave it alone".
		await waitFor(() => expect(sentBody).toEqual({ title: "Survey the vector stores" }));
		await waitFor(() => expect(screen.queryByTestId("edit-dev-workflow-work-item-dialog")).toBeNull());
		await waitFor(() => expect(screen.getByTestId("dev-workflow-title").textContent).toBe("Survey the vector stores"));
	});

	it("keeps the dialog open on a refusal and puts a per-field 400 on the field it names", async () => {
		server.use(
			...baseRoutes(),
			http.patch(localApiPath(`development-workflows/work-items/${workItemId}`), () =>
				HttpResponse.json(
					{
						type: "about:blank",
						title: "One or more validation errors occurred.",
						status: 400,
						detail: "",
						errors: [{ name: "title", reason: "The title is longer than the 200-character limit." }],
					},
					{ status: 400, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		renderPage();
		await openEditDialog();

		fireEvent.change(screen.getByTestId("edit-dev-workflow-work-item-title"), { target: { value: "Survey the vector stores" } });
		fireEvent.click(screen.getByTestId("edit-dev-workflow-work-item-submit"));

		expect(await screen.findByText("The title is longer than the 200-character limit.")).toBeDefined();
		// The edit is unsaved, so the dialog stays put rather than discarding what the operator typed.
		expect(screen.getByTestId("edit-dev-workflow-work-item-dialog")).toBeDefined();
		expect((screen.getByTestId("edit-dev-workflow-work-item-title") as HTMLInputElement).value).toBe("Survey the vector stores");
	});
});

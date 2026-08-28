// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import {
	devWorkflowNodeRunDetail,
	devWorkflowNodeRunSummary,
	devWorkflowRun,
	devWorkflowTestIds,
	devWorkflowWorkItem,
} from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { DevWorkflowDetailPage } from "@/features/devWorkflows/pages/DevWorkflowDetailPage";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";

const navigate = vi.hoisted(() => vi.fn());

vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigate,
}));

// The hub is exercised on its own in useDevWorkflowRunHub.test.tsx; here it only has to not reach for a real socket.
vi.mock("@/core/api/signalr/SharedHubConnection", () => ({
	acquireHubConnection: () => ({
		connection: { state: "Disconnected", on: vi.fn(), off: vi.fn(), invoke: vi.fn() },
		whenStarted: Promise.resolve(),
		onReconnected: () => vi.fn(),
		onReconnecting: () => vi.fn(),
		onClosed: () => vi.fn(),
		release: vi.fn(),
	}),
}));

const { workItem: workItemId, run: runId, nodeRun: nodeRunId } = devWorkflowTestIds;

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

function renderPage(selection: { run?: string; node?: string; tab?: "artifacts" | "events" } = {}) {
	const onSelectionChange = vi.fn();
	renderWithProviders(
		<ConfirmProvider>
			<DevWorkflowDetailPage workItemId={workItemId} selection={selection} onSelectionChange={onSelectionChange} />
		</ConfirmProvider>,
	);
	return { onSelectionChange };
}

describe("DevWorkflowDetailPage", () => {
	beforeEach(() => {
		navigate.mockClear();
	});

	afterEach(() => {
		cleanup();
	});

	it("resolves the latest run when no run is selected and renders its node-run table", async () => {
		server.use(...baseRoutes());
		renderPage();

		expect(await screen.findByTestId("dev-workflow-title")).toBeDefined();
		expect(await screen.findByTestId(`dev-workflow-node-row-${nodeRunId}`)).toBeDefined();
		expect(screen.getByTestId("dev-workflow-run-toolbar")).toBeDefined();
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
		// Y17 renamed the field from `kind`; sending the old name would be accepted as an empty decision.
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
			jsonRoute("get", "development-workflows/definitions", {
				items: [{ id: devWorkflowTestIds.definition, name: "Research → Plan → Approval", version: 1, nodeCount: 3, archived: false }],
			}),
		);
		renderPage();

		expect(await screen.findByTestId("dev-workflow-detail-no-run")).toBeDefined();
		expect(screen.getByTestId("dev-workflow-start-run")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-run-toolbar")).toBeNull();
	});

	it("degrades to a polling notice rather than an error when the hub cannot subscribe", async () => {
		server.use(...baseRoutes());
		renderPage();

		// The mocked connection never reaches Connected, so the hook stays out of "connected" and the page keeps
		// rendering last-good state. The notice is the toolbar's, and it must not be an error.
		await screen.findByTestId("dev-workflow-run-toolbar");
		expect(screen.queryByTestId("dev-workflow-detail-error")).toBeNull();
	});
});

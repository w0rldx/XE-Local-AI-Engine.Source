// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import {
	devWorkflowTestIds,
	devWorkflowWorkItem,
	devWorkflowWorkItemSummary,
} from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { DevWorkflowsPage } from "@/features/devWorkflows/pages/DevWorkflowsPage";
import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { jsonRoute, localApiPath, problemDetailsRoute } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { setupMswServer } from "@/test/UseMswServer";
import { renderWithProviders } from "@/test/RenderWithProviders";

const navigate = vi.hoisted(() => vi.fn());

// The app router is built from routeTree.gen.ts; a unit test only needs the navigate CALL, not a real route match.
vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useNavigate: () => navigate,
}));

const workItemId = devWorkflowTestIds.workItem;
const definitionId = devWorkflowTestIds.definition;

function definitionsRoute() {
	return jsonRoute("get", "development-workflows/definitions", {
		items: [
			{
				id: definitionId,
				// Y5 fixes the seeded template names; the picker shows them verbatim rather than inventing labels.
				name: "Research → Plan → Approval",
				source: "Seeded",
				seedSlug: "research-plan-approval",
				archived: false,
				version: 1,
				nodeCount: 3,
				updatedAtUtc: 1,
			},
		],
	});
}

function projectsRoute() {
	return jsonRoute("get", "development/projects", { items: [] });
}

setupMswServer();

describe("DevWorkflowsPage", () => {
	beforeEach(() => {
		navigate.mockClear();
	});

	afterEach(() => {
		cleanup();
	});

	it("introduces itself with the standard page header", async () => {
		server.use(jsonRoute("get", "development-workflows/work-items", { items: [] }), definitionsRoute(), projectsRoute());
		renderWithProviders(<DevWorkflowsPage />);

		expect(await screen.findByText("Worker Node")).toBeDefined();
		expect(screen.getByRole("heading", { level: 2 }).textContent).toBe("Workflow Runs");
	});

	it("offers a create call to action when there are no work items", async () => {
		server.use(jsonRoute("get", "development-workflows/work-items", { items: [] }), definitionsRoute(), projectsRoute());
		renderWithProviders(<DevWorkflowsPage />);

		await screen.findByTestId("dev-workflows-empty");
		expect(screen.getByTestId("dev-workflows-empty-create")).toBeDefined();
		expect(screen.queryByTestId("dev-workflows-list")).toBeNull();
	});

	it("renders an inline alert with a retry when the list fails", async () => {
		server.use(
			problemDetailsRoute("get", "development-workflows/work-items", 500, { detail: "the store is unavailable" }),
			definitionsRoute(),
			projectsRoute(),
		);
		renderWithProviders(<DevWorkflowsPage />);

		const alert = await screen.findByTestId("dev-workflows-error");
		expect(alert.textContent).toContain("the store is unavailable");
		expect(screen.getByTestId("dev-workflows-retry")).toBeDefined();
	});

	it("counts queued and running separately on the card, never as one 'in progress' figure", async () => {
		server.use(
			jsonRoute("get", "development-workflows/work-items", {
				items: [devWorkflowWorkItemSummary({ runningNodeCount: 1, queuedNodeCount: 4, completedNodeCount: 2, totalNodeCount: 8 })],
			}),
			definitionsRoute(),
			projectsRoute(),
		);
		renderWithProviders(<DevWorkflowsPage />);

		// O9: the node has ONE agent slot, so "5 in progress" would claim parallelism this machine cannot deliver.
		const counts = await screen.findByTestId(`dev-workflow-card-counts-${workItemId}`);
		expect(counts.textContent).toBe("1 running · 4 queued · 2/8 done");
	});

	it("renders a card per work item with both statuses and opens the detail route on click", async () => {
		server.use(
			jsonRoute("get", "development-workflows/work-items", { items: [devWorkflowWorkItemSummary()] }),
			definitionsRoute(),
			projectsRoute(),
		);
		renderWithProviders(<DevWorkflowsPage />);

		const card = await screen.findByTestId(`dev-workflow-card-${workItemId}`);
		expect(card.textContent).toContain("Survey the vector-store options");
		expect(screen.getByTestId(`dev-workflow-card-status-${workItemId}`).textContent).toBe("Active");
		expect(screen.getByTestId(`dev-workflow-card-run-status-${workItemId}`).textContent).toBe("Running");

		fireEvent.click(card);
		expect(navigate).toHaveBeenCalledWith({ to: "/development-workflows/$workItemId", params: { workItemId } });
	});

	it("creates the work item and starts its run as two calls, then opens the detail page", async () => {
		const startBodies: unknown[] = [];
		server.use(
			jsonRoute("get", "development-workflows/work-items", { items: [] }),
			definitionsRoute(),
			projectsRoute(),
			jsonRoute("post", "development-workflows/work-items", devWorkflowWorkItem({ status: "Draft", latestRunId: null, runs: [] })),
			http.post(localApiPath(`development-workflows/work-items/${workItemId}/runs`), async ({ request }) => {
				startBodies.push(await request.json());
				return HttpResponse.json({ runId: devWorkflowTestIds.run }, { status: 202 });
			}),
		);
		renderWithProviders(<DevWorkflowsPage />);

		fireEvent.click(await screen.findByTestId("dev-workflows-create"));
		await screen.findByTestId("create-dev-workflow-work-item-dialog");
		fireEvent.change(screen.getByTestId("create-dev-workflow-work-item-title"), {
			target: { value: "Survey the vector-store options" },
		});
		fireEvent.change(screen.getByTestId("create-dev-workflow-work-item-request"), {
			target: { value: "Compare the options and propose one." },
		});
		fireEvent.click(screen.getByTestId("create-dev-workflow-work-item-definition"));
		fireEvent.click(await screen.findByRole("option", { name: "Research → Plan → Approval", hidden: true }));

		const submit = screen.getByTestId("create-dev-workflow-work-item-submit");
		await waitFor(() => expect((submit as HTMLButtonElement).disabled).toBe(false));
		fireEvent.click(submit);

		await waitFor(() =>
			expect(navigate).toHaveBeenCalledWith({ to: "/development-workflows/$workItemId", params: { workItemId } }),
		);
		// A work item is definition-agnostic at creation (P3 §4.1): the template rides on the SECOND call.
		expect(startBodies).toHaveLength(1);
		expect((startBodies[0] as { definitionId: string }).definitionId).toBe(definitionId);
		expect((startBodies[0] as { operationId: string }).operationId).toMatch(/^[0-9a-f-]{36}$/i);
	});

	it("still opens the detail page when the run fails to start, rather than inviting a duplicate work item", async () => {
		server.use(
			jsonRoute("get", "development-workflows/work-items", { items: [] }),
			definitionsRoute(),
			projectsRoute(),
			jsonRoute("post", "development-workflows/work-items", devWorkflowWorkItem({ status: "Draft", latestRunId: null, runs: [] })),
			problemDetailsRoute("post", `development-workflows/work-items/${workItemId}/runs`, 400, {
				detail: "this graph needs a development project",
			}),
		);
		renderWithProviders(<DevWorkflowsPage />);

		fireEvent.click(await screen.findByTestId("dev-workflows-create"));
		await screen.findByTestId("create-dev-workflow-work-item-dialog");
		fireEvent.change(screen.getByTestId("create-dev-workflow-work-item-title"), { target: { value: "Ship the thing" } });
		fireEvent.change(screen.getByTestId("create-dev-workflow-work-item-request"), { target: { value: "Do it" } });
		fireEvent.click(screen.getByTestId("create-dev-workflow-work-item-definition"));
		fireEvent.click(await screen.findByRole("option", { name: "Research → Plan → Approval", hidden: true }));
		fireEvent.click(screen.getByTestId("create-dev-workflow-work-item-submit"));

		await waitFor(() =>
			expect(navigate).toHaveBeenCalledWith({ to: "/development-workflows/$workItemId", params: { workItemId } }),
		);
	});

	it("reports a failed create in the dialog and keeps it open", async () => {
		server.use(
			jsonRoute("get", "development-workflows/work-items", { items: [] }),
			definitionsRoute(),
			projectsRoute(),
			problemDetailsRoute("post", "development-workflows/work-items", 400, { detail: "the title is too long" }),
		);
		renderWithProviders(<DevWorkflowsPage />);

		fireEvent.click(await screen.findByTestId("dev-workflows-create"));
		await screen.findByTestId("create-dev-workflow-work-item-dialog");
		fireEvent.change(screen.getByTestId("create-dev-workflow-work-item-title"), { target: { value: "Ship the thing" } });
		fireEvent.change(screen.getByTestId("create-dev-workflow-work-item-request"), { target: { value: "Do it" } });
		fireEvent.click(screen.getByTestId("create-dev-workflow-work-item-definition"));
		fireEvent.click(await screen.findByRole("option", { name: "Research → Plan → Approval", hidden: true }));
		fireEvent.click(screen.getByTestId("create-dev-workflow-work-item-submit"));

		const error = await screen.findByTestId("create-dev-workflow-work-item-error");
		expect(error.textContent).toContain("the title is too long");
		expect(navigate).not.toHaveBeenCalled();
	});

	it("keeps the runs list as the default shelf and fetches the rule-set catalogue only when it is opened", async () => {
		let catalogueReads = 0;
		server.use(
			jsonRoute("get", "development-workflows/work-items", { items: [] }),
			definitionsRoute(),
			projectsRoute(),
			http.get(localApiPath("development-workflows/rule-sets"), () => {
				catalogueReads += 1;
				return HttpResponse.json({ items: [] });
			}),
		);
		renderWithProviders(
			<ConfirmProvider>
				<DevWorkflowsPage />
			</ConfirmProvider>,
		);

		// The runs view is what an operator opens this page for, so it is what they get without asking.
		expect(await screen.findByTestId("dev-workflows-empty")).toBeDefined();
		expect(catalogueReads).toBe(0);

		fireEvent.click(screen.getByTestId("dev-workflows-tab-rule-sets"));

		expect(await screen.findByTestId("dev-workflow-rule-sets")).toBeDefined();
		await waitFor(() => expect(catalogueReads).toBe(1));
	});
});

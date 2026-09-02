// @vitest-environment jsdom

// The definition editor writes the document the runtime executes, so what this file owes is the write contract: the
// PUT carries the version the edit was made from, the fields the form does not author survive the round trip, a 409
// offers a reload instead of a save that would discard someone else's work, and a graph the server would refuse never
// leaves the browser.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it } from "vitest";

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { DevWorkflowDefinitionFormPanel } from "@/features/devWorkflows/components/DevWorkflowDefinitionFormPanel";
import type { DevWorkflowGraph } from "@/features/devWorkflows/models/DevWorkflowModels";
import { devWorkflowTestIds } from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const definitionId = devWorkflowTestIds.definition;

/**
 * research → plan, where `plan` carries the three fields the form must round-trip untouched. `toolMode` sits on a
 * Tool node because that is the only node type the server allows it on.
 */
const graph: DevWorkflowGraph = {
	schemaVersion: 1,
	nodes: [
		{ nodeKey: "research", nodeType: "Agent", label: "Research" },
		{
			nodeKey: "plan",
			nodeType: "Tool",
			label: "Plan",
			toolMode: "Apply",
			requiredCapabilities: { gpu: "true" },
			materialization: { templateNodeKey: "research", artifactKind: "TaskPackage", joinNodeKey: "research", maxChildren: 5 },
		},
	],
	edges: [{ from: "research", to: "plan" }],
};

function definitionRoute(overrides: { version?: number; graph?: DevWorkflowGraph } = {}) {
	return jsonRoute("get", `development-workflows/definitions/${definitionId}`, {
		id: definitionId,
		name: "Research → Plan → Approval",
		graph: overrides.graph ?? graph,
		graphHash: "hash",
		source: "Seeded",
		seedSlug: "research-plan-approval",
		archived: false,
		version: overrides.version ?? 3,
		createdAtUtc: 1,
		updatedAtUtc: 2,
	});
}

function optionRoutes() {
	return [jsonRoute("get", "agents", { items: [] }), jsonRoute("get", "local-models", { isAvailable: true, items: [] })];
}

function renderPanel() {
	return renderWithProviders(
		<ConfirmProvider>
			<DevWorkflowDefinitionFormPanel definitionId={definitionId} />
		</ConfirmProvider>,
	);
}

setupMswServer();

describe("DevWorkflowDefinitionFormPanel", () => {
	afterEach(() => {
		cleanup();
	});

	it("prompts for a template rather than rendering an empty form when none is picked", () => {
		renderWithProviders(
			<ConfirmProvider>
				<DevWorkflowDefinitionFormPanel definitionId={undefined} />
			</ConfirmProvider>,
		);

		expect(screen.getByTestId("dev-workflow-definition-form-empty")).toBeDefined();
	});

	it("reports a failed read instead of an empty editor", async () => {
		server.use(
			...optionRoutes(),
			http.get(localApiPath(`development-workflows/definitions/${definitionId}`), () =>
				HttpResponse.json({}, { status: 500 }),
			),
		);
		renderPanel();

		expect(await screen.findByTestId("dev-workflow-definition-form-error")).toBeDefined();
	});

	it("round-trips toolMode, materialization and requiredCapabilities untouched, and carries the version it edited", async () => {
		let sent: { version?: number; name?: string; graph?: DevWorkflowGraph } | undefined;
		server.use(
			...optionRoutes(),
			definitionRoute({ version: 3 }),
			http.put(localApiPath(`development-workflows/definitions/${definitionId}`), async ({ request }) => {
				sent = (await request.json()) as typeof sent;
				return HttpResponse.json({ id: definitionId, name: "Research → Plan → Approval", version: 4 });
			}),
		);
		renderPanel();

		fireEvent.change(await screen.findByTestId("dev-workflow-definition-node-label-0"), { target: { value: "Investigate" } });
		fireEvent.click(screen.getByTestId("dev-workflow-definition-save"));

		await waitFor(() => expect(sent).toBeDefined());
		expect(sent?.version).toBe(3);
		expect(sent?.graph?.nodes?.[0]?.label).toBe("Investigate");
		// The three fields the form shows as badges and never edits must come back exactly as they arrived.
		expect(sent?.graph?.nodes?.[1]?.toolMode).toBe("Apply");
		expect(sent?.graph?.nodes?.[1]?.requiredCapabilities).toEqual({ gpu: "true" });
		expect(sent?.graph?.nodes?.[1]?.materialization?.maxChildren).toBe(5);
	});

	it("shows the fields it will not edit as read-only badges, so nothing looks lost", async () => {
		server.use(...optionRoutes(), definitionRoute());
		renderPanel();

		const badges = await screen.findByTestId("dev-workflow-definition-node-readonly-1");
		expect(badges.textContent).toContain("Apply");
		expect(badges.textContent).toContain("research");
	});

	it("refuses to save a graph the server would reject, naming the node instead of waiting for a 400", async () => {
		let putCalls = 0;
		server.use(
			...optionRoutes(),
			definitionRoute(),
			http.put(localApiPath(`development-workflows/definitions/${definitionId}`), () => {
				putCalls += 1;
				return HttpResponse.json({});
			}),
		);
		renderPanel();

		// Two nodes sharing a key: the server refuses it, and so does the form — before the request.
		fireEvent.change(await screen.findByTestId("dev-workflow-definition-node-key-1"), { target: { value: "research" } });

		expect(screen.getByTestId("dev-workflow-definition-issue-duplicateNodeKey")).toBeDefined();
		expect(screen.getByTestId("dev-workflow-definition-save")).toHaveProperty("disabled", true);
		expect(putCalls).toBe(0);
	});

	it("offers a reload on a 409 rather than a save that would discard the other edit", async () => {
		server.use(
			...optionRoutes(),
			definitionRoute(),
			http.put(localApiPath(`development-workflows/definitions/${definitionId}`), () =>
				HttpResponse.json(
					{ type: "about:blank", title: "Conflict", status: 409, detail: "", conflictType: "DevWorkflowVersionConflict" },
					{ status: 409, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		renderPanel();

		fireEvent.click(await screen.findByTestId("dev-workflow-definition-save"));

		expect(await screen.findByTestId("dev-workflow-definition-conflict")).toBeDefined();
		expect(screen.getByTestId("dev-workflow-definition-reload")).toBeDefined();
	});

	it("renders a 400's own problem detail, because it names a rule this form does not mirror", async () => {
		server.use(
			...optionRoutes(),
			definitionRoute(),
			http.put(localApiPath(`development-workflows/definitions/${definitionId}`), () =>
				HttpResponse.json(
					{
						type: "about:blank",
						title: "Bad Request",
						status: 400,
						detail: "An Apply node must be reached from a human gate.",
					},
					{ status: 400, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		renderPanel();

		fireEvent.click(await screen.findByTestId("dev-workflow-definition-save"));

		const error = await screen.findByTestId("dev-workflow-definition-save-error");
		expect(error.textContent).toContain("An Apply node must be reached from a human gate.");
	});

	it("adds, reorders and removes nodes through keyboard-operable controls", async () => {
		server.use(...optionRoutes(), definitionRoute());
		renderPanel();

		const first = await screen.findByTestId("dev-workflow-definition-node-key-0");
		expect((first as HTMLInputElement).value).toBe("research");

		fireEvent.click(screen.getByTestId("dev-workflow-definition-node-down-0"));
		expect((screen.getByTestId("dev-workflow-definition-node-key-0") as HTMLInputElement).value).toBe("plan");

		fireEvent.click(screen.getByTestId("dev-workflow-definition-add-node"));
		expect(screen.getByTestId("dev-workflow-definition-node-2")).toBeDefined();

		fireEvent.click(screen.getByTestId("dev-workflow-definition-node-remove-2"));
		expect(screen.queryByTestId("dev-workflow-definition-node-2")).toBeNull();
	});

	it("gives every icon control an accessible name", async () => {
		server.use(...optionRoutes(), definitionRoute());
		renderPanel();

		expect((await screen.findAllByLabelText("Move node up")).length).toBe(2);
		expect(screen.getAllByLabelText("Move node down").length).toBeGreaterThan(0);
		expect(screen.getAllByLabelText("Remove node").length).toBe(2);
		expect(screen.getByLabelText("Remove edge")).toBeDefined();
	});

	it("clears an edge's condition when its path is emptied, rather than saving one the runtime cannot evaluate", async () => {
		let sent: { graph?: DevWorkflowGraph } | undefined;
		server.use(
			...optionRoutes(),
			definitionRoute({
				graph: {
					...graph,
					edges: [{ from: "research", to: "plan", condition: { path: "$.decision", op: "eq", value: "Approve" } }],
				},
			}),
			http.put(localApiPath(`development-workflows/definitions/${definitionId}`), async ({ request }) => {
				sent = (await request.json()) as typeof sent;
				return HttpResponse.json({ id: definitionId, version: 4 });
			}),
		);
		renderPanel();

		const path = await screen.findByTestId("dev-workflow-definition-edge-path-0");
		expect((path as HTMLInputElement).value).toBe("$.decision");

		fireEvent.change(path, { target: { value: "" } });
		fireEvent.click(screen.getByTestId("dev-workflow-definition-save"));

		await waitFor(() => expect(sent).toBeDefined());
		expect(sent?.graph?.edges?.[0]?.condition ?? null).toBeNull();
	});

	it("archives behind a confirmation rather than deleting the template outright", async () => {
		let archived = false;
		server.use(
			...optionRoutes(),
			definitionRoute(),
			http.delete(localApiPath(`development-workflows/definitions/${definitionId}`), () => {
				archived = true;
				return new HttpResponse(null, { status: 204 });
			}),
		);
		renderPanel();

		fireEvent.click(await screen.findByTestId("dev-workflow-definition-archive"));
		fireEvent.click(await screen.findByTestId("confirm-accept"));

		await waitFor(() => expect(archived).toBe(true));
	});
});

// @vitest-environment jsdom

// The rule-set catalogue is the one surface in this feature that WRITES policy, so what it owes is the write contract:
// the PUT carries the version the edit was made from, a 409 is reported as "changed elsewhere" rather than as a save to
// retry, and a delete asks first.

import { cleanup, fireEvent, screen, waitFor } from "@testing-library/react";
import { http, HttpResponse } from "msw";
import { afterEach, describe, expect, it, vi } from "vitest";

// Monaco is ~3 MB behind a lazy import and needs a layout engine jsdom does not have. What is under test here is the
// write contract around the body, not the editing surface, so the shared code editor stands in as a textarea.
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

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { DevWorkflowRuleSetsPanel } from "@/features/devWorkflows/components/DevWorkflowRuleSetsPanel";
import { devWorkflowRuleSet, devWorkflowRuleSetSummary, devWorkflowTestIds } from "@/features/devWorkflows/test/DevWorkflowFixtures";
import { localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

const ruleSetId = devWorkflowTestIds.ruleSet;
const projectId = "12121212-1212-4212-8212-121212121212";

setupMswServer();

function listRoute(...items: ReturnType<typeof devWorkflowRuleSetSummary>[]) {
	return http.get(localApiPath("development-workflows/rule-sets"), () => HttpResponse.json({ items }));
}

function detailRoute(overrides: Parameters<typeof devWorkflowRuleSet>[0] = {}) {
	return http.get(localApiPath(`development-workflows/rule-sets/${ruleSetId}`), () =>
		HttpResponse.json(devWorkflowRuleSet(overrides)),
	);
}

function renderPanel() {
	return renderWithProviders(
		<ConfirmProvider>
			<DevWorkflowRuleSetsPanel projects={[{ id: projectId, label: "XE Local AI Engine" }]} />
		</ConfirmProvider>,
	);
}

describe("DevWorkflowRuleSetsPanel", () => {
	afterEach(() => {
		cleanup();
	});

	it("lists a rule set with the scope it actually matches, saying so for an open axis", async () => {
		server.use(listRoute(devWorkflowRuleSetSummary({ scope: { projectIds: [projectId], nodeTypes: ["Agent"] } })));
		renderPanel();

		const scope = await screen.findByTestId(`dev-workflow-rule-set-scope-${ruleSetId}`);
		expect(scope.textContent).toContain("XE Local AI Engine");
		expect(scope.textContent).toContain("Agent");
	});

	it("says an empty scope axis matches everything, rather than leaving it blank as if unset", async () => {
		server.use(listRoute(devWorkflowRuleSetSummary()));
		renderPanel();

		const scope = await screen.findByTestId(`dev-workflow-rule-set-scope-${ruleSetId}`);
		expect(scope.textContent).toBe("every project · every node type");
	});

	it("renders an empty state rather than an empty list", async () => {
		server.use(listRoute());
		renderPanel();

		expect(await screen.findByTestId("dev-workflow-rule-sets-empty")).toBeDefined();
	});

	it("reports a failed catalogue read with a retry rather than an empty page", async () => {
		server.use(http.get(localApiPath("development-workflows/rule-sets"), () => HttpResponse.json({}, { status: 500 })));
		renderPanel();

		expect(await screen.findByTestId("dev-workflow-rule-sets-error")).toBeDefined();
		expect(screen.getByTestId("dev-workflow-rule-sets-retry")).toBeDefined();
	});

	it("sends the version the edit was made from, so a concurrent write is refused instead of overwritten", async () => {
		let sentVersion: unknown;
		server.use(
			listRoute(devWorkflowRuleSetSummary()),
			detailRoute({ version: 7 }),
			http.put(localApiPath(`development-workflows/rule-sets/${ruleSetId}`), async ({ request }) => {
				sentVersion = ((await request.json()) as { version?: number }).version;
				return HttpResponse.json(devWorkflowRuleSet({ version: 8 }));
			}),
		);
		renderPanel();

		fireEvent.click(await screen.findByTestId(`dev-workflow-rule-set-edit-${ruleSetId}`));
		await waitFor(() => expect((screen.getByTestId("dev-workflow-rule-set-name") as HTMLInputElement).value).toBe("House style"));

		fireEvent.click(screen.getByTestId("dev-workflow-rule-set-submit"));

		await waitFor(() => expect(sentVersion).toBe(7));
	});

	it("reports a 409 as 'changed elsewhere' inline, not as a save to try again", async () => {
		server.use(
			listRoute(devWorkflowRuleSetSummary()),
			detailRoute(),
			http.put(localApiPath(`development-workflows/rule-sets/${ruleSetId}`), () =>
				HttpResponse.json(
					{
						type: "about:blank",
						title: "Conflict",
						status: 409,
						detail: "",
						conflictType: "DevWorkflowVersionConflict",
					},
					{ status: 409, headers: { "content-type": "application/problem+json" } },
				),
			),
		);
		renderPanel();

		fireEvent.click(await screen.findByTestId(`dev-workflow-rule-set-edit-${ruleSetId}`));
		await waitFor(() => expect((screen.getByTestId("dev-workflow-rule-set-name") as HTMLInputElement).value).toBe("House style"));

		fireEvent.click(screen.getByTestId("dev-workflow-rule-set-submit"));

		const error = await screen.findByTestId("dev-workflow-rule-set-error");
		expect(error.textContent).toContain("changed elsewhere");
		// The dialog stays open on the operator's unsaved body — a 409 must not throw the edit away.
		expect(screen.getByTestId("dev-workflow-rule-set-dialog")).toBeDefined();
	});

	it("keeps Save unavailable until the rule set has both a name and a body", async () => {
		server.use(listRoute());
		renderPanel();

		fireEvent.click(await screen.findByTestId("dev-workflow-rule-set-create"));
		const submit = await screen.findByTestId("dev-workflow-rule-set-submit");
		expect(submit).toHaveProperty("disabled", true);

		fireEvent.change(screen.getByTestId("dev-workflow-rule-set-name"), { target: { value: "House style" } });
		expect(submit).toHaveProperty("disabled", true);

		fireEvent.change(screen.getByTestId("dev-workflow-rule-set-body"), { target: { value: "Small diffs." } });
		expect(submit).toHaveProperty("disabled", false);
	});

	it("refuses to save a body over the server's own 4096-character ceiling", async () => {
		server.use(listRoute());
		renderPanel();

		fireEvent.click(await screen.findByTestId("dev-workflow-rule-set-create"));
		fireEvent.change(await screen.findByTestId("dev-workflow-rule-set-name"), { target: { value: "House style" } });
		fireEvent.change(screen.getByTestId("dev-workflow-rule-set-body"), { target: { value: "x".repeat(4097) } });

		expect(screen.getByTestId("dev-workflow-rule-set-body-too-long")).toBeDefined();
		expect(screen.getByTestId("dev-workflow-rule-set-submit")).toHaveProperty("disabled", true);
	});

	it("asks before deleting, and deletes when the operator confirms", async () => {
		let deleted = false;
		server.use(
			listRoute(devWorkflowRuleSetSummary()),
			http.delete(localApiPath(`development-workflows/rule-sets/${ruleSetId}`), () => {
				deleted = true;
				return new HttpResponse(null, { status: 204 });
			}),
		);
		renderPanel();

		fireEvent.click(await screen.findByTestId(`dev-workflow-rule-set-delete-${ruleSetId}`));
		fireEvent.click(await screen.findByTestId("confirm-accept"));

		await waitFor(() => expect(deleted).toBe(true));
	});
});

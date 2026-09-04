// @vitest-environment jsdom

// The definition editor writes the document the runtime executes, so what this file owes is the write contract: the
// PUT carries the version the edit was made from, the fields the form does not author survive the round trip, a 409
// offers a reload instead of a save that would discard someone else's work, and a graph the server would refuse never
// leaves the browser.

import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
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
 * `feature-development-v1`'s tail, small enough to assert on: one Agent that decomposes into a `implement → validate`
 * template subtree handed back at `join`, then a human gate and the apply behind it.
 *
 * Every field the form round-trips untouched is on a node the form shows no control for — `modelProfile` and
 * `reasoningEffort` on the Tool node, `toolMode` on the apply, `materialization` on the decomposition — and the ONE
 * Agent node is index 0, so the per-node model and effort pickers exist exactly once.
 *
 * The shape validates clean against the SERVER, which is what lets every save assertion below actually reach the wire:
 * the declared capability is `Network` (no gate required), the apply is reached from a human gate carrying only the
 * approval and from a Tool node in Validate mode, and the materialization joins DOWNSTREAM of the node that produces
 * it. A fixture the server would refuse would pass here — the API is mocked — and be found only in production.
 */
const graph: DevWorkflowGraph = {
	schemaVersion: 1,
	nodes: [
		{
			nodeKey: "decompose",
			nodeType: "Agent",
			label: "Decompose",
			requiredCapabilities: { Network: "reads the package feed while splitting the plan" },
			materialization: { templateNodeKey: "implement", artifactKind: "TaskPackage", joinNodeKey: "join", maxChildren: 5 },
		},
		{ nodeKey: "implement", nodeType: "DevTask", label: "Implement", isTemplate: true },
		{
			nodeKey: "validate",
			nodeType: "Tool",
			label: "Validate",
			isTemplate: true,
			retryTarget: "implement",
			maxLoopIterations: 2,
			modelProfile: "qwen3-30b",
			reasoningEffort: "high",
		},
		{ nodeKey: "join", nodeType: "Join", label: "Every slice implemented" },
		{ nodeKey: "approval", nodeType: "HumanGate", label: "Approve integration" },
		{ nodeKey: "integrate", nodeType: "Tool", toolMode: "Apply", label: "Apply the approved patches" },
	],
	edges: [
		{ from: "decompose", to: "join" },
		{ from: "implement", to: "validate" },
		{ from: "validate", to: "join" },
		{ from: "join", to: "approval" },
		{ from: "approval", to: "integrate", condition: { path: "decision", op: "eq", value: "Approve" } },
	],
};

/** The fixture with one edge condition replaced, so a condition test edits a row without stranding the graph. */
function withCondition(condition: { path: string; op: string; value: unknown }): DevWorkflowGraph {
	return { ...graph, edges: graph.edges?.map((edge, index) => (index === 0 ? { ...edge, condition } : edge)) };
}

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

/** One row of the model list, filled out enough to survive the generated client's own response validation. */
function localModel(modelName: string, displayLabel: string, kind: string) {
	return {
		modelName,
		displayLabel,
		kind,
		detectedKind: kind,
		provider: "llamacpp",
		capabilities: [],
		isSelected: false,
		isReasoningCapable: false,
		isToolCapable: true,
		isOverridden: false,
	};
}

function optionRoutes() {
	return [
		jsonRoute("get", "agents", { items: [] }),
		jsonRoute("get", "models", {
			isAvailable: true,
			items: [localModel("qwen3-30b", "Qwen3 30B", "Chat"), localModel("nomic-embed", "Nomic Embed", "Embedding")],
		}),
	];
}

function renderPanel() {
	return renderWithProviders(
		<ConfirmProvider>
			<DevWorkflowDefinitionFormPanel definitionId={definitionId} />
		</ConfirmProvider>,
	);
}

/**
 * Opens one edge row's operator picker and answers ITS dropdown. Every row keeps its option list mounted, so a bare
 * text query matches five identical lists; the open combobox names its own listbox through `aria-controls`.
 */
function openOperator(index: number): ReturnType<typeof within> {
	const input = screen.getByTestId(`dev-workflow-definition-edge-op-${index}`);
	fireEvent.click(input);
	const listbox = document.getElementById(input.getAttribute("aria-controls") ?? "");
	if (!listbox) {
		throw new Error(`The operator picker on edge ${index} named no open listbox.`);
	}
	return within(listbox);
}

setupMswServer();

/** Loads a definition carrying one conditional edge, types `text` into the value cell, saves, and returns the body. */
async function editConditionValue(
	condition: { path: string; op: string; value: unknown },
	text: string,
): Promise<{ graph?: DevWorkflowGraph } | undefined> {
	let sent: { graph?: DevWorkflowGraph } | undefined;
	server.use(
		...optionRoutes(),
		definitionRoute({ graph: withCondition(condition) }),
		http.put(localApiPath(`development-workflows/definitions/${definitionId}`), async ({ request }) => {
			sent = (await request.json()) as typeof sent;
			return HttpResponse.json({ id: definitionId, version: 4 });
		}),
	);
	renderPanel();

	fireEvent.change(await screen.findByTestId("dev-workflow-definition-edge-value-0"), { target: { value: text } });
	fireEvent.click(screen.getByTestId("dev-workflow-definition-save"));
	await waitFor(() => expect(sent).toBeDefined());
	cleanup();
	return sent;
}

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

	it("round-trips every field it does not author untouched, and carries the version it edited", async () => {
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

		fireEvent.change(await screen.findByTestId("dev-workflow-definition-node-label-0"), { target: { value: "Split the plan" } });
		fireEvent.click(screen.getByTestId("dev-workflow-definition-save"));

		await waitFor(() => expect(sent).toBeDefined());
		expect(sent?.version).toBe(3);
		expect(sent?.graph?.nodes?.[0]?.label).toBe("Split the plan");
		// Everything the form does not author must come back exactly as it arrived — the Tool node's model and effort
		// included, which this form shows no control for precisely because that lane dispatches on neither.
		expect(sent?.graph?.nodes?.[5]?.toolMode).toBe("Apply");
		expect(sent?.graph?.nodes?.[0]?.requiredCapabilities).toEqual({ Network: "reads the package feed while splitting the plan" });
		expect(sent?.graph?.nodes?.[0]?.materialization?.maxChildren).toBe(5);
		expect(sent?.graph?.nodes?.[2]?.modelProfile).toBe("qwen3-30b");
		expect(sent?.graph?.nodes?.[2]?.reasoningEffort).toBe("high");
		expect(sent?.graph?.nodes?.[2]?.maxLoopIterations).toBe(2);
		// And an untouched template keeps its waiver ABSENT — an explicit `null` would be a document saying something
		// it never said, and `?? null` would have passed on one.
		expect("allowUngatedWrites" in (sent?.graph ?? {})).toBe(false);
	});

	it("shows the fields it will not edit as read-only badges, so nothing looks lost", async () => {
		server.use(...optionRoutes(), definitionRoute());
		renderPanel();

		expect((await screen.findByTestId("dev-workflow-definition-node-readonly-5")).textContent).toContain("Apply");
		expect(screen.getByTestId("dev-workflow-definition-node-readonly-0").textContent).toContain("implement");
	});

	it("offers only this node's CHAT models as a per-node model, because a non-chat one is a run that fails at dispatch", async () => {
		server.use(...optionRoutes(), definitionRoute());
		renderPanel();

		expect(await screen.findByText("Qwen3 30B")).toBeDefined();
		expect(screen.queryByText("Nomic Embed")).toBeNull();
	});

	it("offers the model and effort pins on an Agent node only, because no other lane dispatches on them", async () => {
		// Node 0 is the only Agent; node 1 is a DevTask and node 2 a Tool, which run Dev Mode's own coder and commands.
		server.use(...optionRoutes(), definitionRoute());
		renderPanel();

		expect(await screen.findByTestId("dev-workflow-definition-node-model-0")).toBeDefined();
		expect(screen.getByTestId("dev-workflow-definition-node-effort-0")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-definition-node-model-1")).toBeNull();
		expect(screen.queryByTestId("dev-workflow-definition-node-effort-1")).toBeNull();
		expect(screen.queryByTestId("dev-workflow-definition-node-model-2")).toBeNull();
		expect(screen.queryByTestId("dev-workflow-definition-node-effort-2")).toBeNull();
	});

	it("sends the per-node model and reasoning effort an operator picked, which is what that node's session runs on", async () => {
		let sent: { graph?: DevWorkflowGraph } | undefined;
		server.use(
			...optionRoutes(),
			definitionRoute(),
			http.put(localApiPath(`development-workflows/definitions/${definitionId}`), async ({ request }) => {
				sent = (await request.json()) as typeof sent;
				return HttpResponse.json({ id: definitionId, name: "Research → Plan → Approval", version: 4 });
			}),
		);
		renderPanel();

		fireEvent.click(await screen.findByTestId("dev-workflow-definition-node-model-0"));
		fireEvent.click(await screen.findByText("Qwen3 30B"));
		fireEvent.click(screen.getByTestId("dev-workflow-definition-node-effort-0"));
		fireEvent.click(screen.getByText("medium"));
		fireEvent.click(screen.getByTestId("dev-workflow-definition-save"));

		await waitFor(() => expect(sent).toBeDefined());
		expect(sent?.graph?.nodes?.[0]?.modelProfile).toBe("qwen3-30b");
		expect(sent?.graph?.nodes?.[0]?.reasoningEffort).toBe("medium");
	});

	it("offers auto in the per-node reasoning effort menu and sends it as written", async () => {
		// The node is agent-bound, so its turn always carries a pinned model: authoring `auto` buys the effort ladder
		// and never a model swap. All the form has to do is stop hiding the token.
		let sent: { graph?: DevWorkflowGraph } | undefined;
		server.use(
			...optionRoutes(),
			definitionRoute(),
			http.put(localApiPath(`development-workflows/definitions/${definitionId}`), async ({ request }) => {
				sent = (await request.json()) as typeof sent;
				return HttpResponse.json({ id: definitionId, name: "Research → Plan → Approval", version: 4 });
			}),
		);
		renderPanel();

		fireEvent.click(await screen.findByTestId("dev-workflow-definition-node-effort-0"));
		fireEvent.click(screen.getByText("auto"));
		fireEvent.click(screen.getByTestId("dev-workflow-definition-save"));

		await waitFor(() => expect(sent).toBeDefined());
		expect(sent?.graph?.nodes?.[0]?.reasoningEffort).toBe("auto");
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
		fireEvent.change(await screen.findByTestId("dev-workflow-definition-node-key-1"), { target: { value: "decompose" } });

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
		expect((first as HTMLInputElement).value).toBe("decompose");

		fireEvent.click(screen.getByTestId("dev-workflow-definition-node-down-0"));
		expect((screen.getByTestId("dev-workflow-definition-node-key-0") as HTMLInputElement).value).toBe("implement");

		fireEvent.click(screen.getByTestId("dev-workflow-definition-add-node"));
		expect(screen.getByTestId("dev-workflow-definition-node-6")).toBeDefined();

		fireEvent.click(screen.getByTestId("dev-workflow-definition-node-remove-6"));
		expect(screen.queryByTestId("dev-workflow-definition-node-6")).toBeNull();
	});

	it("gives every icon control an accessible name", async () => {
		server.use(...optionRoutes(), definitionRoute());
		renderPanel();

		expect((await screen.findAllByLabelText("Move node up")).length).toBe(6);
		expect(screen.getAllByLabelText("Move node down").length).toBeGreaterThan(0);
		expect(screen.getAllByLabelText("Remove node").length).toBe(6);
		expect(screen.getAllByLabelText("Remove edge").length).toBe(5);
	});

	it("keeps a boolean and a number scalar through the value cell, because the server compares by JSON kind", async () => {
		const sent = await editConditionValue({ path: "$.ok", op: "eq", value: true }, "false");
		expect(sent?.graph?.edges?.[0]?.condition?.value).toBe(false);

		const numeric = await editConditionValue({ path: "$.count", op: "gte", value: 2 }, "5");
		expect(numeric?.graph?.edges?.[0]?.condition?.value).toBe(5);
	});

	it("leaves a plain word a string, because a decision token is one", async () => {
		const sent = await editConditionValue({ path: "$.decision", op: "eq", value: "Approve" }, "Reject");

		expect(sent?.graph?.edges?.[0]?.condition?.value).toBe("Reject");
	});

	it("does not touch the value when only the operator changed — a rewritten scalar makes the edge silently dead", async () => {
		let sent: { graph?: DevWorkflowGraph } | undefined;
		server.use(
			...optionRoutes(),
			definitionRoute({ graph: withCondition({ path: "$.ok", op: "eq", value: true }) }),
			http.put(localApiPath(`development-workflows/definitions/${definitionId}`), async ({ request }) => {
				sent = (await request.json()) as typeof sent;
				return HttpResponse.json({ id: definitionId, version: 4 });
			}),
		);
		renderPanel();

		await screen.findByTestId("dev-workflow-definition-edge-0");
		fireEvent.click(openOperator(0).getByText("ne"));
		fireEvent.click(screen.getByTestId("dev-workflow-definition-save"));

		await waitFor(() => expect(sent).toBeDefined());
		expect(sent?.graph?.edges?.[0]?.condition?.op).toBe("ne");
		expect(sent?.graph?.edges?.[0]?.condition?.value).toBe(true);
	});

	it("offers the operator as the server's closed set rather than free text", async () => {
		server.use(...optionRoutes(), definitionRoute());
		renderPanel();

		await screen.findByTestId("dev-workflow-definition-edge-0");
		const options = openOperator(0);

		for (const operator of ["eq", "ne", "gt", "gte", "lt", "lte", "exists", "notExists"]) {
			expect(options.getByText(operator)).toBeDefined();
		}
	});

	it("clears an edge's condition when its path is emptied, rather than saving one the runtime cannot evaluate", async () => {
		let sent: { graph?: DevWorkflowGraph } | undefined;
		server.use(
			...optionRoutes(),
			definitionRoute({ graph: withCondition({ path: "$.decision", op: "eq", value: "Approve" }) }),
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

	it("declares a capability with the reason its author wrote, and shows what each node can change", async () => {
		let sent: { graph?: DevWorkflowGraph } | undefined;
		server.use(
			...optionRoutes(),
			definitionRoute(),
			http.put(localApiPath(`development-workflows/definitions/${definitionId}`), async ({ request }) => {
				sent = (await request.json()) as typeof sent;
				return HttpResponse.json({ id: definitionId, version: 4 });
			}),
		);
		renderPanel();

		// The effect badges are DERIVED the way the invariants derive them: the apply node writes, the validation reads
		// and reaches the network (it names no command, so it inherits the project profile's set).
		expect((await screen.findByTestId("dev-workflow-definition-node-effects-5")).textContent).toContain(
			"writes the repository or runs commands",
		);
		expect(screen.getByTestId("dev-workflow-definition-node-effects-2").textContent).toContain("reads the repository");

		fireEvent.click(screen.getByTestId("dev-workflow-definition-node-capabilities-0"));
		fireEvent.click(await screen.findByText("Orchestration"));
		fireEvent.change(screen.getByTestId("dev-workflow-definition-node-capability-reason-1-0"), {
			target: { value: "hands slices to the coder agents" },
		});
		fireEvent.click(screen.getByTestId("dev-workflow-definition-save"));

		await waitFor(() => expect(sent).toBeDefined());
		expect(sent?.graph?.nodes?.[0]?.requiredCapabilities).toEqual({
			Network: "reads the package feed while splitting the plan",
			Orchestration: "hands slices to the coder agents",
		});
	});

	it("refuses a declared write nothing gates, and takes the template's waiver as the answer", async () => {
		let sent: { graph?: DevWorkflowGraph } | undefined;
		server.use(
			...optionRoutes(),
			definitionRoute(),
			http.put(localApiPath(`development-workflows/definitions/${definitionId}`), async ({ request }) => {
				sent = (await request.json()) as typeof sent;
				return HttpResponse.json({ id: definitionId, version: 4 });
			}),
		);
		renderPanel();

		// `decompose` is the entry node, so nothing gates it: declaring a write there is GRAPH-C4-2, before the request.
		fireEvent.click(await screen.findByTestId("dev-workflow-definition-node-capabilities-0"));
		fireEvent.click(await screen.findByText("WriteExecute"));

		const issue = screen.getByTestId("dev-workflow-definition-issue-ungatedWrite");
		expect(issue.textContent).toContain("decompose");
		expect(screen.getByTestId("dev-workflow-definition-save")).toHaveProperty("disabled", true);

		fireEvent.click(screen.getByTestId("dev-workflow-definition-allow-ungated-writes"));
		expect(screen.queryByTestId("dev-workflow-definition-issue-ungatedWrite")).toBeNull();

		fireEvent.change(screen.getByTestId("dev-workflow-definition-node-capability-reason-1-0"), {
			target: { value: "runs the release script" },
		});
		fireEvent.click(screen.getByTestId("dev-workflow-definition-save"));

		await waitFor(() => expect(sent).toBeDefined());
		expect(sent?.graph?.allowUngatedWrites).toBe(true);
	});

	it("offers the fix-loop cap only where there is a loop to bound, and clears it with the target", async () => {
		let sent: { graph?: DevWorkflowGraph } | undefined;
		server.use(
			...optionRoutes(),
			definitionRoute(),
			http.put(localApiPath(`development-workflows/definitions/${definitionId}`), async ({ request }) => {
				sent = (await request.json()) as typeof sent;
				return HttpResponse.json({ id: definitionId, version: 4 });
			}),
		);
		renderPanel();

		// Node 2 routes back to `implement`; node 0 routes nowhere, and the server refuses a cap on a node that does.
		expect(await screen.findByTestId("dev-workflow-definition-node-max-loops-2")).toBeDefined();
		expect(screen.queryByTestId("dev-workflow-definition-node-max-loops-0")).toBeNull();

		fireEvent.change(screen.getByTestId("dev-workflow-definition-node-max-loops-2"), { target: { value: "3" } });
		fireEvent.click(screen.getByTestId("dev-workflow-definition-save"));

		await waitFor(() => expect(sent).toBeDefined());
		expect(sent?.graph?.nodes?.[2]?.maxLoopIterations).toBe(3);

		// Clearing the target takes the cap with it. This is the branch that keeps the server from 400ing over a field
		// the form has just hidden, and without this half a refactor that dropped `maxLoopIterations: null` stays green.
		const retryTarget = screen.getByTestId("dev-workflow-definition-node-retry-target-2");
		const clear = retryTarget.parentElement?.querySelector("button");
		expect(clear).toBeDefined();
		fireEvent.click(clear as HTMLButtonElement);

		expect(screen.queryByTestId("dev-workflow-definition-node-max-loops-2")).toBeNull();
		fireEvent.click(screen.getByTestId("dev-workflow-definition-save"));

		// The second PUT is the one with no target; waiting on that is what tells it apart from the first.
		await waitFor(() => expect(sent?.graph?.nodes?.[2]?.retryTarget ?? null).toBeNull());
		expect(sent?.graph?.nodes?.[2]?.maxLoopIterations ?? null).toBeNull();
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

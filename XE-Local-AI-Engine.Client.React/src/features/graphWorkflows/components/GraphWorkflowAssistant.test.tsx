// @vitest-environment jsdom

// The Assistant tab inside the real editor mode: the side-pane focus rules (a selection turns to the Inspector, a reply
// never turns away from it but marks the tab), and one designer turn end to end over MSW: send, cancel, the busy node
// and the refused cloud model. The canvas side of a proposal (diff, accept, undo, stale) is the editor hook's and the
// canvas's own tests; here only the review bar's wiring to it is checked.

import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { delay, HttpResponse, http } from "msw";
import { useState } from "react";
import { afterEach, describe, expect, it, vi } from "vitest";

// Same stand-ins as the page test: jsdom gives React Flow a 0×0 viewport, so the canvas is one button per node.
vi.mock("@xyflow/react", async (importOriginal) => ({
	...(await importOriginal<typeof import("@xyflow/react")>()),
	Background: () => null,
	Controls: () => null,
	Handle: () => null,
	ReactFlowProvider: ({ children }: { readonly children: React.ReactNode }) => children,
	useReactFlow: () => ({ screenToFlowPosition: () => ({ x: 0, y: 0 }), fitView: () => Promise.resolve(true) }),
	ReactFlow: ({
		nodes,
		onNodeClick,
	}: {
		readonly nodes?: readonly { readonly id: string; readonly data: Record<string, unknown> }[];
		readonly onNodeClick?: (event: unknown, node: { readonly id: string }) => void;
	}) => (
		<div data-testid="react-flow">
			{(nodes ?? []).map((node) => (
				<button
					key={node.id}
					type="button"
					data-testid={`flow-node-${node.id}`}
					data-diff={String(node.data["diff"] ?? "")}
					onClick={(event) => onNodeClick?.(event, node)}
				>
					{node.id}
				</button>
			))}
		</div>
	),
}));

vi.mock("@/core/ui/components/CodeEditor/CodeEditor", () => ({
	CodeEditor: ({ value, "data-testid": testId }: { value: string; "data-testid"?: string }) => (
		<textarea data-testid={testId} readOnly={true} value={value} />
	),
}));

vi.mock("@tanstack/react-router", async (importOriginal) => ({
	...(await importOriginal<typeof import("@tanstack/react-router")>()),
	useBlocker: () => ({ status: "idle", proceed: undefined, reset: undefined }),
}));

import { ConfirmProvider } from "@/core/ui/components/ConfirmProvider/ConfirmProvider";
import { GraphWorkflowEditorMode } from "@/features/graphWorkflows/components/GraphWorkflowEditorMode";
import type {
	GraphWorkflowSelection,
	RunGraphWorkflowDesignerTurnRequest,
} from "@/features/graphWorkflows/models/GraphWorkflowModels";
import {
	designerTurnResponse,
	eightNodeGraph,
	graphWorkflowDefinition,
	graphWorkflowDefinitionSummary,
	graphWorkflowTestIds,
	graphWorkflowTools,
} from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import en from "@/locales/en.json";
import { domainErrorRoute, jsonRoute, localApiPath } from "@/test/msw/Handlers";
import { server } from "@/test/msw/Server";
import { renderWithProviders } from "@/test/RenderWithProviders";
import { setupMswServer } from "@/test/UseMswServer";

setupMswServer();

const definitionId = graphWorkflowTestIds.definition;
const designer = en.pages.graphWorkflows.designer;
const turnPath = "graph-workflows/designer/turn";

/** The routes the editor reads on open, a node selection's pickers, and the model list the Assistant asks for. */
function editorRoutes() {
	return [
		jsonRoute("get", "graph-workflows/definitions", { definitions: [graphWorkflowDefinitionSummary()] }),
		jsonRoute("get", `graph-workflows/definitions/${definitionId}`, graphWorkflowDefinition({ graph: eightNodeGraph })),
		jsonRoute("get", "graph-workflows/tools", graphWorkflowTools()),
		jsonRoute("get", "agents", { items: [] }),
		jsonRoute("get", "models", {
			isAvailable: true,
			items: [
				{
					modelName: "qwen-27b",
					displayLabel: null,
					isSelected: true,
					kind: "Chat",
					detectedKind: "Chat",
					capabilities: [],
					isReasoningCapable: true,
					isToolCapable: true,
					isOverridden: false,
					provider: "llamacpp",
				},
			],
		}),
	];
}

/** The selection is URL state on the real page; a stateful wrapper stands in for the router. */
function Editor({ isNarrow }: { readonly isNarrow: boolean }) {
	const [selection, setSelection] = useState<GraphWorkflowSelection>({ definitionId });
	return (
		<ConfirmProvider>
			<GraphWorkflowEditorMode selection={selection} onSelectionChange={setSelection} isNarrow={isNarrow} />
		</ConfirmProvider>
	);
}

async function renderEditor(isNarrow = false) {
	const rendered = renderWithProviders(<Editor isNarrow={isNarrow} />);
	await screen.findByTestId("flow-node-analyze");
	return rendered;
}

async function openAssistantAndType(text: string): Promise<void> {
	fireEvent.click(screen.getByTestId("gw-page-tab-assistant"));
	await waitFor(() => {
		expect(screen.getByTestId<HTMLInputElement>("gw-assistant-model").value).toBe("qwen-27b");
	});
	fireEvent.change(screen.getByTestId("gw-assistant-input"), { target: { value: text } });
}

function tabSelected(tab: "inspector" | "assistant"): boolean {
	return screen.getByTestId(`gw-page-tab-${tab}`).getAttribute("aria-selected") === "true";
}

afterEach(() => {
	cleanup();
});

describe("GraphWorkflowEditorMode Assistant tab", () => {
	it("turns to the Inspector when a node is selected", async () => {
		server.use(...editorRoutes());
		await renderEditor();
		fireEvent.click(screen.getByTestId("gw-page-tab-assistant"));

		fireEvent.click(screen.getByTestId("flow-node-analyze"));

		await waitFor(() => {
			expect(tabSelected("inspector")).toBe(true);
		});
		expect(await screen.findByTestId("gw-node-config")).toBeTruthy();
	});

	it("opens the Assistant from a node's panel with a question about it, and names the node", async () => {
		server.use(...editorRoutes());
		await renderEditor();
		fireEvent.click(screen.getByTestId("flow-node-analyze"));

		fireEvent.click(await screen.findByTestId("gw-node-config-ask-ai"));

		expect(tabSelected("assistant")).toBe(true);
		expect(screen.getByTestId<HTMLTextAreaElement>("gw-assistant-input").value).toBe(
			designer.askAboutNodePrompt.replace("{{key}}", "analyze"),
		);
		expect(screen.getByTestId("gw-assistant-selected-node").textContent).toBe("analyze");
	});

	it("sends the conversation and the working graph, shows the reply, and puts the proposal up for review", async () => {
		let sent: RunGraphWorkflowDesignerTurnRequest | undefined;
		server.use(
			...editorRoutes(),
			http.post(localApiPath(turnPath), async ({ request }) => {
				sent = (await request.json()) as RunGraphWorkflowDesignerTurnRequest;
				return HttpResponse.json(designerTurnResponse());
			}),
		);
		await renderEditor();
		await openAssistantAndType("Replace the file lookup with a note.");

		fireEvent.click(screen.getByTestId("gw-assistant-send"));

		expect(await screen.findByText(designerTurnResponse().reply)).toBeTruthy();
		expect(sent?.modelName).toBe("qwen-27b");
		expect(sent?.definitionId).toBe(definitionId);
		expect(sent?.messages).toEqual([{ role: "user", text: "Replace the file lookup with a note." }]);
		expect(sent?.graph?.nodes?.map((node) => node.key)).toContain("lookup");
		expect(screen.getByTestId("gw-review-bar")).toBeTruthy();
		expect(screen.getByTestId("flow-node-note").getAttribute("data-diff")).toBe("added");

		fireEvent.click(screen.getByTestId("gw-review-accept"));

		expect(await screen.findByTestId("gw-review-undo")).toBeTruthy();
		expect(screen.getByTestId("flow-node-note").getAttribute("data-diff")).toBe("");
		expect(screen.queryByTestId("flow-node-lookup")).toBeNull();
	});

	it("names a graph-level settings change in the review and shows the proposal's settings read-only", async () => {
		server.use(
			...editorRoutes(),
			jsonRoute(
				"post",
				turnPath,
				designerTurnResponse({
					proposal: { ...eightNodeGraph, kind: "Chat", chat: { requireRerunConfirmation: false } },
					changes: [{ kind: "graph", key: "settings", change: "modified" }],
				}),
			),
		);
		await renderEditor();
		await openAssistantAndType("Make this a chat workflow.");

		fireEvent.click(screen.getByTestId("gw-assistant-send"));

		const settings = await screen.findByTestId("gw-review-settings");
		expect(settings.textContent).toContain("Kind: Standard → Chat");
		expect(settings.textContent).toContain("Rerun confirmation: on → off");
		expect(screen.getByTestId("gw-review-summary").textContent).toBe(
			designer.review.summary.replace("{{added}}", "0").replace("{{modified}}", "0").replace("{{removed}}", "0"),
		);

		fireEvent.click(screen.getByTestId("gw-page-settings"));

		const rerun = await screen.findByTestId<HTMLInputElement>("gw-settings-require-rerun-confirmation");
		expect(rerun.checked).toBe(false);
		expect(rerun.disabled).toBe(true);
	});

	it("lists a lint finding left after the retries and refuses Accept", async () => {
		server.use(
			...editorRoutes(),
			jsonRoute(
				"post",
				turnPath,
				designerTurnResponse({
					validation: {
						valid: false,
						errors: [{ key: "merge", message: "Every inbound edge comes from an exclusive branch." }],
						warnings: [],
						nodeCount: 8,
					},
				}),
			),
		);
		await renderEditor();
		await openAssistantAndType("Add a join.");

		fireEvent.click(screen.getByTestId("gw-assistant-send"));

		expect((await screen.findByTestId("gw-review-invalid")).textContent).toContain(
			"merge: Every inbound edge comes from an exclusive branch.",
		);
		expect(screen.getByTestId<HTMLButtonElement>("gw-review-accept").disabled).toBe(true);
	});

	it("never puts an empty reply into the history the next turn sends", async () => {
		const sent: RunGraphWorkflowDesignerTurnRequest[] = [];
		server.use(
			...editorRoutes(),
			http.post(localApiPath(turnPath), async ({ request }) => {
				sent.push((await request.json()) as RunGraphWorkflowDesignerTurnRequest);
				return HttpResponse.json(designerTurnResponse({ reply: "  \n ", proposal: null, changes: [], validation: null }));
			}),
		);
		await renderEditor();
		await openAssistantAndType("First question.");
		fireEvent.click(screen.getByTestId("gw-assistant-send"));
		await waitFor(() => {
			expect(screen.getByTestId("graph-workflow-canvas").getAttribute("data-mode")).toBe("editing");
		});
		expect(sent).toHaveLength(1);
		expect(screen.queryAllByTestId("gw-assistant-message-assistant")).toHaveLength(0);

		fireEvent.change(screen.getByTestId("gw-assistant-input"), { target: { value: "Second question." } });
		fireEvent.click(screen.getByTestId("gw-assistant-send"));

		await waitFor(() => {
			expect(sent).toHaveLength(2);
		});
		expect(sent[1]?.messages).toEqual([
			{ role: "user", text: "First question." },
			{ role: "user", text: "Second question." },
		]);
	});

	it("shows a long reply whole but replays it cut to the endpoint's 8000-character cap", async () => {
		const longReply = "x".repeat(9000);
		const sent: RunGraphWorkflowDesignerTurnRequest[] = [];
		server.use(
			...editorRoutes(),
			http.post(localApiPath(turnPath), async ({ request }) => {
				sent.push((await request.json()) as RunGraphWorkflowDesignerTurnRequest);
				return HttpResponse.json(designerTurnResponse({ reply: longReply, proposal: null, changes: [], validation: null }));
			}),
		);
		await renderEditor();
		await openAssistantAndType("Explain everything.");
		fireEvent.click(screen.getByTestId("gw-assistant-send"));
		expect((await screen.findByTestId("gw-assistant-message-assistant")).textContent).toBe(longReply);

		fireEvent.change(screen.getByTestId("gw-assistant-input"), { target: { value: "Shorter, please." } });
		fireEvent.click(screen.getByTestId("gw-assistant-send"));

		await waitFor(() => {
			expect(sent).toHaveLength(2);
		});
		const replayed = sent[1]?.messages?.[1]?.text ?? "";
		expect(replayed).toHaveLength(8000);
		expect(replayed.endsWith("…")).toBe(true);
		expect(replayed.startsWith("x".repeat(7999))).toBe(true);
	});

	it("marks the Assistant tab instead of turning to it when the reply lands while the Inspector is open", async () => {
		let release: () => void = () => undefined;
		const answered = new Promise<void>((resolve) => {
			release = resolve;
		});
		server.use(
			...editorRoutes(),
			http.post(localApiPath(turnPath), async () => {
				await answered;
				return HttpResponse.json(designerTurnResponse({ proposal: null, changes: [] }));
			}),
		);
		await renderEditor();
		await openAssistantAndType("What does this workflow do?");
		fireEvent.click(screen.getByTestId("gw-assistant-send"));
		await screen.findByTestId("gw-assistant-pending");

		fireEvent.click(screen.getByTestId("gw-page-tab-inspector"));
		release();

		expect(await screen.findByTestId("gw-page-assistant-unread")).toBeTruthy();
		expect(tabSelected("inspector")).toBe(true);

		fireEvent.click(screen.getByTestId("gw-page-tab-assistant"));

		await waitFor(() => {
			expect(screen.queryByTestId("gw-page-assistant-unread")).toBeNull();
		});
		// A reply without a proposal leaves nothing to review and the canvas editable.
		expect(screen.queryByTestId("gw-review-bar")).toBeNull();
		expect(screen.getByTestId<HTMLButtonElement>("graph-workflow-auto-arrange").disabled).toBe(false);
	});

	it("locks the canvas while the turn runs, and Cancel unlocks it with the message back in the box", async () => {
		server.use(
			...editorRoutes(),
			http.post(localApiPath(turnPath), async () => {
				await delay("infinite");
				return HttpResponse.json(designerTurnResponse());
			}),
		);
		await renderEditor();
		await openAssistantAndType("Add an approval step.");
		fireEvent.click(screen.getByTestId("gw-assistant-send"));

		await screen.findByTestId("gw-assistant-pending");
		expect(screen.getByTestId<HTMLButtonElement>("graph-workflow-auto-arrange").disabled).toBe(true);
		expect(screen.getByTestId("graph-workflow-canvas").getAttribute("data-mode")).toBe("generating");

		fireEvent.click(screen.getByTestId("gw-assistant-cancel"));

		await waitFor(() => {
			expect(screen.getByTestId("graph-workflow-canvas").getAttribute("data-mode")).toBe("editing");
		});
		expect(screen.getByTestId<HTMLTextAreaElement>("gw-assistant-input").value).toBe("Add an approval step.");
		expect(screen.queryByTestId("gw-assistant-messages")).toBeNull();
		expect(screen.queryByTestId("gw-assistant-error")).toBeNull();
	});

	it("turns off every save that reloads the definition while the turn runs", async () => {
		server.use(
			...editorRoutes(),
			http.post(localApiPath(turnPath), async () => {
				await delay("infinite");
				return HttpResponse.json(designerTurnResponse());
			}),
		);
		await renderEditor();
		expect(screen.getByTestId<HTMLButtonElement>("gw-page-rename").disabled).toBe(false);
		expect(screen.getByTestId<HTMLButtonElement>("gw-page-save-as").disabled).toBe(false);
		await openAssistantAndType("Add an approval step.");

		fireEvent.click(screen.getByTestId("gw-assistant-send"));

		await screen.findByTestId("gw-assistant-pending");
		expect(screen.getByTestId<HTMLButtonElement>("gw-page-rename").disabled).toBe(true);
		expect(screen.getByTestId<HTMLButtonElement>("gw-page-save-as").disabled).toBe(true);
		expect(screen.getByTestId<HTMLButtonElement>("gw-page-save").disabled).toBe(true);
	});

	it("cancels the turn and says so when the workflow is reloaded under it, with the message back in the box", async () => {
		let version = 1;
		let turns = 0;
		server.use(
			// Ahead of the fixed definition route in `editorRoutes`: the first matching handler answers.
			http.get(localApiPath(`graph-workflows/definitions/${definitionId}`), () =>
				HttpResponse.json(graphWorkflowDefinition({ graph: eightNodeGraph, version })),
			),
			...editorRoutes(),
			http.post(localApiPath(turnPath), async () => {
				turns += 1;
				await delay("infinite");
				return HttpResponse.json(designerTurnResponse());
			}),
		);
		const { queryClient } = await renderEditor();
		await openAssistantAndType("Add an approval step.");
		fireEvent.click(screen.getByTestId("gw-assistant-send"));
		await screen.findByTestId("gw-assistant-pending");

		// A save made elsewhere bumps the version; the refetch answers it and the page loads it over the canvas.
		version = 2;
		await queryClient.invalidateQueries();

		expect((await screen.findByTestId("gw-assistant-reloaded")).textContent).toBe(designer.reloaded);
		expect(screen.getByTestId("graph-workflow-canvas").getAttribute("data-mode")).toBe("editing");
		expect(screen.getByTestId<HTMLTextAreaElement>("gw-assistant-input").value).toBe("Add an approval step.");
		expect(screen.queryByTestId("gw-assistant-messages")).toBeNull();
		expect(turns).toBe(1);
	});

	it("says the node is busy on a 409, and leaves the graph editable", async () => {
		server.use(
			...editorRoutes(),
			domainErrorRoute("post", turnPath, 409, { code: "NodeBusy", message: "A run holds the node.", grant: null }),
		);
		await renderEditor();
		await openAssistantAndType("Add an approval step.");

		fireEvent.click(screen.getByTestId("gw-assistant-send"));

		expect((await screen.findByTestId("gw-assistant-busy")).textContent).toBe(designer.busy);
		expect(screen.getByTestId("graph-workflow-canvas").getAttribute("data-mode")).toBe("editing");
	});

	it("names the local-data grant when a cloud model is refused", async () => {
		server.use(
			...editorRoutes(),
			domainErrorRoute("post", turnPath, 403, {
				code: "CloudLocalDataWithheld",
				message: "This cloud model may not read node-local data.",
				grant: "LocalData",
			}),
		);
		await renderEditor();
		await openAssistantAndType("Add an approval step.");

		fireEvent.click(screen.getByTestId("gw-assistant-send"));

		const refusal = await screen.findByTestId("gw-assistant-cloud-refused");
		expect(screen.getByTestId("gw-assistant-cloud-refused-detail").textContent).toBe(
			"This cloud model may not read node-local data.",
		);
		// The refusal quotes the two switches by the labels the operator finds them under.
		expect(refusal.textContent).toContain(en.pages.externalProviders.cloudGrants.localData);
		expect(refusal.textContent).toContain(en.pages.nodeSettings.fields.allowCloudModelAccess.label);
	});

	it("offers another try when the model's output was unusable", async () => {
		server.use(
			...editorRoutes(),
			domainErrorRoute("post", turnPath, 422, { code: "Unparseable", message: "No usable envelope.", grant: null }),
		);
		await renderEditor();
		await openAssistantAndType("Add an approval step.");

		fireEvent.click(screen.getByTestId("gw-assistant-send"));

		expect((await screen.findByTestId("gw-assistant-unparseable")).textContent).toBe(designer.unparseable);
		expect(screen.getByTestId<HTMLTextAreaElement>("gw-assistant-input").value).toBe("Add an approval step.");
	});

	it("says the provider rejected the request on a 502, with the message back in the box", async () => {
		server.use(
			...editorRoutes(),
			domainErrorRoute("post", turnPath, 502, { code: "ProviderFailed", message: "Upstream said 400.", grant: null }),
		);
		await renderEditor();
		await openAssistantAndType("Add an approval step.");

		fireEvent.click(screen.getByTestId("gw-assistant-send"));

		expect((await screen.findByTestId("gw-assistant-provider-failed")).textContent).toBe(designer.providerFailed);
		expect(screen.getByTestId<HTMLTextAreaElement>("gw-assistant-input").value).toBe("Add an approval step.");
		expect(screen.getByTestId("graph-workflow-canvas").getAttribute("data-mode")).toBe("editing");
	});

	it("renders the same tabs in the drawer on a narrow screen, opened from the toolbar", async () => {
		server.use(...editorRoutes());
		await renderEditor(true);

		fireEvent.click(screen.getByTestId("gw-page-open-assistant"));

		const drawer = await screen.findByTestId("gw-page-config-drawer");
		expect(within(drawer).getByTestId("gw-assistant")).toBeTruthy();
		expect(within(drawer).getByTestId("gw-page-tab-assistant").getAttribute("aria-selected")).toBe("true");
	});
});

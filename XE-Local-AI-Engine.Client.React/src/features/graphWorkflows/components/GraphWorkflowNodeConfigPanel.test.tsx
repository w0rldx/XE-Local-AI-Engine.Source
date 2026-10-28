// @vitest-environment jsdom

// The config panel is where a graph stops being a picture and becomes a document the runtime executes, so what it owes
// is: a body for every one of the eight kinds (a kind with no case renders nothing and an operator cannot configure
// it), a JSON field that reports a parse failure instead of throwing the canvas away, a key rename that surfaces the
// editor's refusal rather than silently corrupting the edges, a `maxAttempts` that writes nothing on mount, and a Tool
// form driven by the server's own tool list and parameter schema.

import { fireEvent, screen, within } from "@testing-library/react";
import { useState } from "react";
import { describe, expect, it, vi } from "vitest";

// Monaco is ~3 MB behind a lazy import and needs a layout engine jsdom does not have. The JSON fields' contract here is
// the message they produce, not the editing surface, so the shared editor stands in as a textarea.
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
import { GraphWorkflowNodeConfigPanel } from "@/features/graphWorkflows/components/GraphWorkflowNodeConfigPanel";
import {
	defaultNodeData,
	type GraphWorkflowCanvasNodeData,
	type GraphWorkflowGraphSettings,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { type GraphWorkflowNodeKind, graphWorkflowNodeKinds } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { graphWorkflowTools } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import en from "@/locales/en.json";
import { renderWithProviders } from "@/test/RenderWithProviders";

const tools = graphWorkflowTools().tools ?? [];

interface Handlers {
	readonly onChange?: (patch: Partial<GraphWorkflowCanvasNodeData>) => void;
	readonly onRename?: (to: string) => "ok" | "collision" | "invalid";
	readonly onRemove?: () => void;
	readonly graphSettings?: GraphWorkflowGraphSettings;
	readonly tools?: typeof tools;
	readonly allowsActions?: boolean;
	readonly decisionModelOptionsReady?: boolean;
}

function renderPanel(node: GraphWorkflowCanvasNodeData, handlers: Handlers = {}) {
	return renderWithProviders(
		<ConfirmProvider>
			<GraphWorkflowNodeConfigPanel
				node={node}
				issues={[]}
				onChange={handlers.onChange ?? vi.fn()}
				onRename={handlers.onRename ?? (() => "ok")}
				onRemove={handlers.onRemove ?? vi.fn()}
				tools={handlers.tools ?? tools}
				allowsActions={handlers.allowsActions}
				agentOptions={[
					{ value: "agent-id", label: "Reviewer", requiresTools: true },
					{ value: "plain-id", label: "Plain", requiresTools: false },
				]}
				modelOptions={[
					{ value: "qwen3-8b", label: "Qwen3 8B", isToolCapable: true },
					{ value: "tiny", label: "Tiny", isToolCapable: false },
					{ value: "gpt-5.5", label: "gpt-5.5", isToolCapable: true, cloudProvider: "Codex" },
				]}
				decisionModelOptions={[{ value: "julia-1", label: "Julia-1" }]}
				decisionModelOptionsReady={handlers.decisionModelOptionsReady}
				graphSettings={handlers.graphSettings}
			/>
		</ConfirmProvider>,
	);
}

function StatefulStartPanel() {
	const [node, setNode] = useState(() => defaultNodeData("Start", "start"));
	return (
		<ConfirmProvider>
			<GraphWorkflowNodeConfigPanel
				node={node}
				issues={[]}
				onChange={(patch) => setNode((current) => ({ ...current, ...patch }) as GraphWorkflowCanvasNodeData)}
				onRename={() => "ok"}
				onRemove={vi.fn()}
				tools={tools}
				agentOptions={[]}
				modelOptions={[]}
			/>
		</ConfirmProvider>
	);
}

function StatefulLlmPanel() {
	const [node, setNode] = useState(() => defaultNodeData("LlmCall", "llm-call-1"));
	return (
		<ConfirmProvider>
			<GraphWorkflowNodeConfigPanel
				node={node}
				issues={[]}
				onChange={(patch) => setNode((current) => ({ ...current, ...patch }) as GraphWorkflowCanvasNodeData)}
				onRename={() => "ok"}
				onRemove={vi.fn()}
				tools={tools}
				agentOptions={[]}
				modelOptions={[]}
			/>
		</ConfirmProvider>
	);
}

// One body per kind. The union is closed, so a kind that falls through to the pass-through text is a kind whose
// configuration an operator can never reach.
const bodyTestIdByKind: Record<GraphWorkflowNodeKind, string> = {
	Start: "gw-node-config-input-schema",
	Agent: "gw-node-config-instructions",
	LlmCall: "gw-node-config-llm-prompt",
	Tool: "gw-node-config-tool",
	Condition: "gw-node-config-path",
	Parallel: "gw-node-config-passthrough",
	Join: "gw-node-config-passthrough",
	Pause: "gw-node-config-prompt",
	End: "gw-node-config-outcome",
	ChatInput: "gw-node-config-chat-prompt",
	DecisionModel: "gw-node-config-question",
};

describe("GraphWorkflowNodeConfigPanel", () => {
	it.each(graphWorkflowNodeKinds)("renders the %s body and the common header", (kind) => {
		renderPanel(defaultNodeData(kind, `${kind.toLowerCase()}-1`));

		expect((screen.getByTestId("gw-node-config-key") as HTMLInputElement).value).toBe(`${kind.toLowerCase()}-1`);
		expect(screen.getByTestId("gw-node-config-join")).toBeTruthy();
		expect(screen.getByTestId(bodyTestIdByKind[kind])).toBeTruthy();
	});

	it.each([
		["a tool-using agent on a model override without tools", "agent-id", "tiny", true],
		["a tool-using agent on a tool-capable override", "agent-id", "qwen3-8b", false],
		["an agent that lists no tools", "plain-id", "tiny", false],
		["no model override, so the server decides", "agent-id", null, false],
		["the default persona, which needs no tools", null, "tiny", false],
	])("warns about tools only for %s", (_case, agentDefinitionId, model, warns) => {
		const node = { ...defaultNodeData("Agent", "agent-1"), agentDefinitionId, model } as GraphWorkflowCanvasNodeData;
		renderPanel(node);

		const warning = screen.queryByTestId("gw-node-config-tools-warning");
		expect(warning !== null).toBe(warns);
		if (warns) {
			expect(warning?.textContent).toBe(en.pages.graphWorkflows.config.toolsUnavailableWarning);
		}
	});

	it.each([
		["Agent", "gw-node-config-model"],
		["LlmCall", "gw-node-config-llm-model"],
		["DecisionModel", "gw-node-config-decision-model"],
	] as const)("marks a cloud model and says when it may run on the %s picker", (kind, testId) => {
		renderPanel({ ...defaultNodeData(kind, "node-1"), model: "gpt-5.5" } as GraphWorkflowCanvasNodeData);

		expect((screen.getByTestId(testId) as HTMLInputElement).value).toBe("gpt-5.5 · Codex (cloud)");
		expect(screen.getByText(en.pages.graphWorkflows.config.cloudModelHelp)).toBeTruthy();
	});

	it("reports invalid JSON in responseJsonSchema as a field message instead of throwing", () => {
		const node = { ...defaultNodeData("Agent", "agent-1"), responseJsonSchema: '{ "type": ' } as GraphWorkflowCanvasNodeData;
		renderPanel(node);

		// Nothing until the field is touched — a half-typed document is not an error yet.
		expect(screen.queryByTestId("gw-node-config-response-schema-error")).toBeNull();

		fireEvent.change(screen.getByTestId("gw-node-config-response-schema"), { target: { value: '{ "type": "obj' } });

		expect(screen.getByTestId("gw-node-config-response-schema-error").textContent).toBe(
			"Enter a JSON object schema, or leave it empty.",
		);
	});

	it("adds a Start alias on Enter and names a duplicate that differs only in case and spacing", () => {
		renderWithProviders(<StatefulStartPanel />);

		// TagsInput commits on Enter only while its own combobox is open (Mantine 9.5.2), so open it first.
		const aliases = screen.getByTestId("gw-node-config-aliases");
		for (const alias of ["lights on", "Lights  ON"]) {
			fireEvent.click(aliases);
			fireEvent.change(aliases, { target: { value: alias } });
			fireEvent.keyDown(aliases, { key: "Enter" });
		}

		expect(screen.getByText("lights on")).toBeTruthy();
		expect(screen.getByText("An alias appears twice (case and spacing are ignored).")).toBeTruthy();
	});

	it("keeps LLM sampling overrides collapsed until requested", () => {
		renderPanel(defaultNodeData("LlmCall", "llm-call-1"));

		const advanced = screen.getByText("Advanced").closest("button");
		expect(advanced?.getAttribute("aria-expanded")).toBe("false");
		fireEvent.click(advanced as HTMLButtonElement);

		expect(advanced?.getAttribute("aria-expanded")).toBe("true");
		expect(screen.getByTestId("gw-node-config-sampling-temperature")).toBeTruthy();
		expect(screen.getByText("Limits this request's prompt budget; it does not resize the server context window.")).toBeTruthy();
	});

	it("keeps a trailing newline while entering stop sequences and publishes multiple lines", () => {
		renderWithProviders(<StatefulLlmPanel />);
		fireEvent.click(screen.getByText("Advanced"));
		const stop = screen.getByTestId("gw-node-config-sampling-stop") as HTMLTextAreaElement;

		fireEvent.change(stop, { target: { value: "first\n" } });
		expect(stop.value).toBe("first\n");
		fireEvent.change(stop, { target: { value: "first\nsecond" } });
		expect(stop.value).toBe("first\nsecond");

		fireEvent.change(stop, { target: { value: "" } });
		expect(stop.value).toBe("");
	});

	it("flags a key that breaks the charset at the keystroke and does not rename on it", () => {
		const onRename = vi.fn<(to: string) => "ok" | "collision" | "invalid">(() => "ok");
		renderPanel(defaultNodeData("Agent", "agent-1"), { onRename });

		fireEvent.change(screen.getByTestId("gw-node-config-key"), { target: { value: "agent 1!" } });

		expect(screen.getByText("Use letters, digits, hyphens and underscores, up to 64 characters.")).toBeTruthy();
		expect(onRename).not.toHaveBeenCalled();
	});

	it("surfaces a collision the editor refused when the key is committed", () => {
		const onRename = vi.fn<(to: string) => "ok" | "collision" | "invalid">(() => "collision");
		renderPanel(defaultNodeData("Agent", "agent-1"), { onRename });

		const keyInput = screen.getByTestId("gw-node-config-key");
		fireEvent.change(keyInput, { target: { value: "agent-2" } });
		fireEvent.blur(keyInput);

		expect(onRename).toHaveBeenCalledWith("agent-2");
		expect(screen.getByText("That key already belongs to another node or edge.")).toBeTruthy();
	});

	it("shows the kind's default max attempts without writing it", () => {
		const onChange = vi.fn();
		renderPanel({ ...defaultNodeData("Agent", "agent-1"), maxAttempts: undefined } as GraphWorkflowCanvasNodeData, {
			onChange,
		});

		const attempts = screen.getByTestId("gw-node-config-attempts") as HTMLInputElement;
		expect(attempts.value).toBe("");
		// 3 for an Agent, shown as the placeholder because "unset" and "3" are different documents.
		expect(attempts.placeholder).toBe("3");

		fireEvent.change(attempts, { target: { value: "5" } });

		expect(onChange).toHaveBeenCalledWith({ maxAttempts: 5 });
	});

	it("leaves a stored max-attempts value above the field's bound untouched on blur", () => {
		const onChange = vi.fn();
		// Mantine's NumberInput clamps on blur by DEFAULT. A stored 200 tabbed through would come back as the maximum,
		// dirtying the graph with an edit the operator never made — and the server has no upper bound to justify it.
		renderPanel({ ...defaultNodeData("Agent", "agent-1"), maxAttempts: 200 } as GraphWorkflowCanvasNodeData, { onChange });

		const attempts = screen.getByTestId("gw-node-config-attempts") as HTMLInputElement;
		fireEvent.focus(attempts);
		fireEvent.blur(attempts);

		expect(attempts.value).toBe("200");
		expect(onChange).not.toHaveBeenCalled();
		expect(screen.getByText("Enter a whole number between 1 and 100.")).toBeTruthy();
	});

	it("reports an empty decision set once the operator has changed it", () => {
		// A Pause offering nothing is the state the server refuses; the panel held the message and had no way to show it,
		// because nothing marked the group touched. `onChange` is a spy here, so the set stays empty across the click.
		const node = { ...defaultNodeData("Pause", "pause-1"), allowedDecisions: [] } as GraphWorkflowCanvasNodeData;
		renderPanel(node);

		expect(screen.queryByText("Offer at least one decision.")).toBeNull();

		fireEvent.click(screen.getByTestId("gw-node-config-decision-Approve"));

		expect(screen.getByText("Offer at least one decision.")).toBeTruthy();
	});

	it("reports a binding row with no path once the row has been left", () => {
		const node = {
			...defaultNodeData("Tool", "tool-1"),
			toolName: "read_file",
			argumentBindings: [{ parameter: "path", path: "" }],
		} as GraphWorkflowCanvasNodeData;
		renderPanel(node);

		fireEvent.blur(screen.getByTestId("gw-node-config-binding-path-0"));

		expect(screen.getByTestId("gw-node-config-bindings-error").textContent).toBe("Enter the path this argument reads from.");
	});

	it("says argument bindings are resolved when the run reaches the node, not when the graph is saved", () => {
		renderPanel({ ...defaultNodeData("Tool", "tool-1"), toolName: "read_file" } as GraphWorkflowCanvasNodeData);

		const help = en.pages.graphWorkflows.config.argumentBindingsHelp;
		expect(help).toBe("Replaces one argument with a value read from this node's input document when the run reaches it.");
		expect(screen.getByText(help)).toBeTruthy();
	});

	it("keeps showing a configured tool that the node no longer offers", () => {
		const node = { ...defaultNodeData("Tool", "tool-1"), toolName: "retired_tool" } as GraphWorkflowCanvasNodeData;
		renderPanel(node);

		// Blank would read as "no tool configured" while the graph still carries one.
		expect((screen.getByTestId("gw-node-config-tool") as HTMLInputElement).value).toBe("retired_tool");
	});

	it("offers exactly the tools it was given", async () => {
		renderPanel(defaultNodeData("Tool", "tool-1"));

		const input = screen.getByTestId("gw-node-config-tool");
		fireEvent.click(input);
		// Scoped to THIS combobox's own dropdown: the header's join-policy Select renders options too, and an unscoped
		// role query would assert over both lists.
		const dropdown = document.getElementById(input.getAttribute("aria-controls") ?? "");
		const options = await within(dropdown as HTMLElement).findAllByRole("option", { hidden: true });

		expect(options.map((option) => option.textContent)).toEqual(tools.map((tool) => tool.name));
	});

	it("seeds the arguments document from the chosen tool's parameter schema", async () => {
		const onChange = vi.fn();
		renderPanel(defaultNodeData("Tool", "tool-1"), { onChange });

		fireEvent.click(screen.getByTestId("gw-node-config-tool"));
		fireEvent.click(await screen.findByRole("option", { name: "read_file", hidden: true }));

		expect(onChange).toHaveBeenCalledWith({ toolName: "read_file", argumentsJson: '{\n  "path": ""\n}' });
	});

	it("lists the schema's property names in the binding parameter picker", async () => {
		const node = {
			...defaultNodeData("Tool", "tool-1"),
			toolName: "read_file",
			argumentBindings: [{ parameter: "", path: "" }],
		} as GraphWorkflowCanvasNodeData;
		renderPanel(node);

		fireEvent.click(screen.getByTestId("gw-node-config-binding-parameter-0"));

		expect(await screen.findByRole("option", { name: "path", hidden: true })).toBeTruthy();
	});

	describe("an action tool (ADR 0021)", () => {
		const config = en.pages.graphWorkflows.config;
		const lights = {
			name: "custom__lights",
			description: "Turns the lights on.",
			parameterSchema: '{"type":"object","properties":{}}',
			requiresAllowedUrls: false,
			isAction: true,
		};
		const withAction = [...tools, lights];
		const actionNode = { ...defaultNodeData("Tool", "tool-1"), toolName: lights.name } as GraphWorkflowCanvasNodeData;

		it("is offered disabled and labelled while the definition does not allow actions", async () => {
			renderPanel(defaultNodeData("Tool", "tool-1"), { tools: withAction });

			const input = screen.getByTestId("gw-node-config-tool");
			fireEvent.click(input);
			const dropdown = document.getElementById(input.getAttribute("aria-controls") ?? "");
			const option = await within(dropdown as HTMLElement).findByRole("option", {
				name: `${lights.name} · ${config.actionBadge}`,
				hidden: true,
			});

			expect(option.hasAttribute("data-combobox-disabled")).toBe(true);
			expect(screen.getByTestId("gw-node-config-tool-actions-disabled").textContent).toBe(config.actionsDisabledHint);
		});

		it("is pickable once the definition allows actions", async () => {
			const onChange = vi.fn();
			renderPanel(defaultNodeData("Tool", "tool-1"), { tools: withAction, allowsActions: true, onChange });

			fireEvent.click(screen.getByTestId("gw-node-config-tool"));
			fireEvent.click(await screen.findByRole("option", { name: `${lights.name} · ${config.actionBadge}`, hidden: true }));

			expect(onChange).toHaveBeenCalledWith(expect.objectContaining({ toolName: lights.name }));
			expect(screen.queryByTestId("gw-node-config-tool-actions-disabled")).toBeNull();
		});

		it("on a node, shows the badge and the note, and the save gate's refusal while actions are not allowed", () => {
			renderPanel(actionNode, { tools: withAction });

			expect(screen.getByTestId("gw-node-config-tool-action-badge").textContent).toBe(config.actionBadge);
			expect(screen.getByText(config.actionNote)).toBeTruthy();
			expect(screen.getByTestId("gw-node-config-tool-action-refused").textContent).toBe(
				`Tool '${lights.name}' is an action with a side effect, so this node needs 'Allow actions' on this definition.`,
			);
		});

		it("on a node of a definition that allows actions, shows no refusal", () => {
			renderPanel(actionNode, { tools: withAction, allowsActions: true });

			expect(screen.getByTestId("gw-node-config-tool-action-badge")).toBeTruthy();
			expect(screen.queryByTestId("gw-node-config-tool-action-refused")).toBeNull();
		});
	});

	it("never offers Answer on a Pause: only a ChatInput takes it", () => {
		renderPanel(defaultNodeData("Pause", "pause-1"));

		expect(screen.getByTestId("gw-node-config-decision-Approve")).toBeTruthy();
		expect(screen.queryByTestId("gw-node-config-decision-Answer")).toBeNull();
	});

	it("edits a ChatInput's question for the user", () => {
		const onChange = vi.fn();
		renderPanel(defaultNodeData("ChatInput", "chat-input-1"), { onChange, graphSettings: { kind: "Chat" } });

		expect(screen.getByText("What to ask the user")).toBeTruthy();
		fireEvent.change(screen.getByTestId("gw-node-config-chat-prompt"), { target: { value: "Which database?" } });

		expect(onChange).toHaveBeenCalledWith({ prompt: "Which database?" });
	});

	describe("DecisionModel form", () => {
		function decisionNode(labels: readonly string[]): GraphWorkflowCanvasNodeData {
			return {
				...defaultNodeData("DecisionModel", "decision-model-1"),
				question: "Which?",
				labels,
			} as GraphWorkflowCanvasNodeData;
		}

		it("edits the question and appends an empty label row", () => {
			const onChange = vi.fn();
			renderPanel(decisionNode(["coding"]), { onChange });

			fireEvent.change(screen.getByTestId("gw-node-config-question"), { target: { value: "Classify it" } });
			fireEvent.click(screen.getByTestId("gw-node-config-label-add"));

			expect(onChange).toHaveBeenCalledWith({ question: "Classify it" });
			expect(onChange).toHaveBeenCalledWith({ labels: ["coding", ""] });
		});

		it("edits and removes one label, and says why the list is refused once touched", () => {
			const onChange = vi.fn();
			renderPanel(decisionNode(["coding", "general"]), { onChange });

			expect(screen.queryByTestId("gw-node-config-labels-error")).toBeNull();
			fireEvent.change(screen.getByTestId("gw-node-config-label-1"), { target: { value: "coding" } });
			expect(onChange).toHaveBeenLastCalledWith({ labels: ["coding", "coding"] });
			fireEvent.click(screen.getByTestId("gw-node-config-label-remove-0"));
			expect(onChange).toHaveBeenLastCalledWith({ labels: ["general"] });
		});

		it("trims a label on blur, so an output.choice edge can match it", () => {
			const onChange = vi.fn();
			renderPanel(decisionNode(["coding ", "general"]), { onChange });

			fireEvent.blur(screen.getByTestId("gw-node-config-label-0"));

			expect(onChange).toHaveBeenLastCalledWith({ labels: ["coding", "general"] });
			expect(screen.getByRole("group", { name: "Labels" })).toBeTruthy();
		});

		it("shows the labels message from the bundle for a list the parser refuses", () => {
			renderPanel(decisionNode(["only"]));

			fireEvent.change(screen.getByTestId("gw-node-config-label-0"), { target: { value: "solo" } });

			expect(screen.getByTestId("gw-node-config-labels-error").textContent).toBe(
				"Use between 2 and 32 distinct, non-empty labels of at most 64 characters.",
			);
		});

		it("offers the llm provider under its readable name", async () => {
			renderPanel(decisionNode(["a", "b"]));

			const input = screen.getByTestId("gw-node-config-decision-provider");
			fireEvent.click(input);
			const dropdown = document.getElementById(input.getAttribute("aria-controls") ?? "");
			const options = await within(dropdown as HTMLElement).findAllByRole("option", { hidden: true });

			expect(options.map((option) => option.textContent)).toEqual(["Language model", "Native decision model (systemone)"]);
		});

		async function optionsOf(testId: string): Promise<(string | null)[]> {
			const input = screen.getByTestId(testId);
			fireEvent.click(input);
			const dropdown = document.getElementById(input.getAttribute("aria-controls") ?? "");
			const options = await within(dropdown as HTMLElement).findAllByRole("option", { hidden: true });
			return options.map((option) => option.textContent);
		}

		it("shows a description field per choice label and moves it with a renamed label", () => {
			const onChange = vi.fn();
			renderPanel(
				{ ...decisionNode(["coding", "general"]), criteria: { coding: "writes code" } } as GraphWorkflowCanvasNodeData,
				{
					onChange,
				},
			);

			expect((screen.getByTestId("gw-node-config-label-criterion-0") as HTMLInputElement).value).toBe("writes code");
			fireEvent.change(screen.getByTestId("gw-node-config-label-criterion-1"), { target: { value: "anything else" } });
			expect(onChange).toHaveBeenLastCalledWith({ criteria: { coding: "writes code", general: "anything else" } });
			fireEvent.change(screen.getByTestId("gw-node-config-label-0"), { target: { value: "code" } });
			expect(onChange).toHaveBeenLastCalledWith({ labels: ["code", "general"], criteria: { code: "writes code" } });
			fireEvent.click(screen.getByTestId("gw-node-config-label-remove-0"));
			expect(onChange).toHaveBeenLastCalledWith({ labels: ["general"], criteria: {} });
		});

		it("relabels the list as ordered levels for score and hides it for noul", () => {
			const score = renderPanel({
				...decisionNode(["low", "high"]),
				questionType: "score",
				provider: "systemone",
			} as GraphWorkflowCanvasNodeData);
			expect(screen.getByRole("group", { name: "Levels, lowest first" })).toBeTruthy();
			expect(screen.queryByTestId("gw-node-config-label-criterion-0")).toBeNull();
			score.unmount();

			renderPanel({ ...decisionNode(["kept"]), questionType: "noul", provider: "systemone" } as GraphWorkflowCanvasNodeData);
			expect(screen.queryByTestId("gw-node-config-label-add")).toBeNull();
			expect(screen.queryByTestId("gw-node-config-label-0")).toBeNull();
		});

		it("offers the three question types and writes the picked one", async () => {
			const onChange = vi.fn();
			renderPanel(decisionNode(["a", "b"]), { onChange });

			expect(await optionsOf("gw-node-config-decision-type")).toEqual([
				"Choice (pick one label)",
				"Yes/no",
				"Score (ordered levels)",
			]);
			fireEvent.click(screen.getByRole("option", { name: "Yes/no", hidden: true }));
			expect(onChange).toHaveBeenLastCalledWith({ questionType: "noul" });
		});

		it("lists only Decision-kind models for systemone, with the uncalibrated hint", async () => {
			renderPanel({ ...decisionNode(["a", "b"]), provider: "systemone" } as GraphWorkflowCanvasNodeData);

			expect(screen.getByText("Native decision model; probabilities are model-reported and uncalibrated.")).toBeTruthy();
			expect(await optionsOf("gw-node-config-decision-model")).toEqual(["Julia-1"]);
		});

		it("drops a chat model when the provider switches to systemone", async () => {
			const onChange = vi.fn();
			renderPanel({ ...decisionNode(["a", "b"]), model: "qwen3-8b" } as GraphWorkflowCanvasNodeData, { onChange });

			await optionsOf("gw-node-config-decision-provider");
			fireEvent.click(screen.getByRole("option", { name: "Native decision model (systemone)", hidden: true }));
			expect(onChange).toHaveBeenLastCalledWith({ provider: "systemone", model: null });
		});

		it("keeps the model when the effective provider does not change, or the target list has not loaded", async () => {
			const onChange = vi.fn();
			const same = renderPanel({ ...decisionNode(["a", "b"]), model: "qwen3-8b" } as GraphWorkflowCanvasNodeData, {
				onChange,
			});
			// null → "llm" is the same provider: picking it must not erase the model.
			await optionsOf("gw-node-config-decision-provider");
			fireEvent.click(screen.getByRole("option", { name: "Language model", hidden: true }));
			expect(onChange).toHaveBeenLastCalledWith({ provider: "llm" });
			same.unmount();

			renderPanel({ ...decisionNode(["a", "b"]), model: "qwen3-8b" } as GraphWorkflowCanvasNodeData, {
				onChange,
				decisionModelOptionsReady: false,
			});
			await optionsOf("gw-node-config-decision-provider");
			fireEvent.click(screen.getByRole("option", { name: "Native decision model (systemone)", hidden: true }));
			expect(onChange).toHaveBeenLastCalledWith({ provider: "systemone" });
		});

		it("does not overwrite a label's own description when another label is renamed onto it", () => {
			const onChange = vi.fn();
			renderPanel({ ...decisionNode(["a", "b"]), criteria: { a: "first", b: "second" } } as GraphWorkflowCanvasNodeData, {
				onChange,
			});

			fireEvent.change(screen.getByTestId("gw-node-config-label-0"), { target: { value: "b" } });
			expect(onChange).toHaveBeenLastCalledWith({ labels: ["b", "b"], criteria: { b: "second" } });
		});
	});

	describe("chat switches", () => {
		it("are absent on a Standard graph", () => {
			renderPanel(defaultNodeData("Agent", "agent-1"));

			expect(screen.queryByTestId("gw-node-config-publish-to-chat")).toBeNull();
			expect(screen.queryByTestId("gw-node-config-include-attachments")).toBeNull();
		});

		it("stay reachable on a Standard graph while a refused flag is on, so it can be turned off", () => {
			const onChange = vi.fn();
			renderPanel({ ...defaultNodeData("End", "done"), publishToChat: true } as GraphWorkflowCanvasNodeData, { onChange });

			fireEvent.click(screen.getByTestId("gw-node-config-publish-to-chat"));

			// Off is the Standard-graph default, so it is written as absent rather than `false`.
			expect(onChange).toHaveBeenCalledWith({ publishToChat: undefined });
		});

		it("writes an End's publish flag back as absent when it returns to the Chat-graph default", () => {
			const onChange = vi.fn();
			renderPanel({ ...defaultNodeData("End", "done"), publishToChat: false } as GraphWorkflowCanvasNodeData, {
				onChange,
				graphSettings: { kind: "Chat" },
			});

			fireEvent.click(screen.getByTestId("gw-node-config-publish-to-chat"));

			expect(onChange).toHaveBeenCalledWith({ publishToChat: undefined });
		});

		it("shows an End's absent flag as the Chat-graph default, on", () => {
			renderPanel(defaultNodeData("End", "done"), { graphSettings: { kind: "Chat" } });

			expect(screen.getByText("Post the result to the chat")).toBeTruthy();
			expect((screen.getByTestId("gw-node-config-publish-to-chat") as HTMLInputElement).checked).toBe(true);
			expect(screen.queryByTestId("gw-node-config-include-attachments")).toBeNull();
		});

		it("keeps includeAttachments off and disabled until the graph accepts attachments", () => {
			renderPanel(defaultNodeData("LlmCall", "llm-call-1"), { graphSettings: { kind: "Chat" } });

			expect((screen.getByTestId("gw-node-config-publish-to-chat") as HTMLInputElement).checked).toBe(false);
			const include = screen.getByTestId("gw-node-config-include-attachments") as HTMLInputElement;
			expect(include.disabled).toBe(true);
			expect(screen.getByText("Turn on “Accepts attachments” in the workflow settings first.")).toBeTruthy();
		});

		it("turns includeAttachments on once the graph accepts attachments", () => {
			const onChange = vi.fn();
			renderPanel(defaultNodeData("Agent", "agent-1"), {
				onChange,
				graphSettings: { kind: "Chat", chat: { acceptsAttachments: true } },
			});

			fireEvent.click(screen.getByTestId("gw-node-config-include-attachments"));

			expect(onChange).toHaveBeenCalledWith({ includeAttachments: true });
		});
	});
});

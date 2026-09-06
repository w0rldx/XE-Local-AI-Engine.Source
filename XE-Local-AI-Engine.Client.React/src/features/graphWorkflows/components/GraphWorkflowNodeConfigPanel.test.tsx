// @vitest-environment jsdom

// The config panel is where a graph stops being a picture and becomes a document the runtime executes, so what it owes
// is: a body for every one of the eight kinds (a kind with no case renders nothing and an operator cannot configure
// it), a JSON field that reports a parse failure instead of throwing the canvas away, a key rename that surfaces the
// editor's refusal rather than silently corrupting the edges, a `maxAttempts` that writes nothing on mount, and a Tool
// form driven by the server's own tool list and parameter schema.

import { fireEvent, screen, within } from "@testing-library/react";
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
import { type GraphWorkflowCanvasNodeData, defaultNodeData } from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { type GraphWorkflowNodeKind, graphWorkflowNodeKinds } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { graphWorkflowTools } from "@/features/graphWorkflows/test/GraphWorkflowFixtures";
import { renderWithProviders } from "@/test/RenderWithProviders";

const tools = graphWorkflowTools().tools ?? [];

interface Handlers {
	readonly onChange?: (patch: Partial<GraphWorkflowCanvasNodeData>) => void;
	readonly onRename?: (to: string) => "ok" | "collision" | "invalid";
	readonly onRemove?: () => void;
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
				tools={tools}
				agentOptions={[{ value: "agent-id", label: "Reviewer" }]}
				modelOptions={[{ value: "qwen3-8b", label: "Qwen3 8B" }]}
			/>
		</ConfirmProvider>,
	);
}

// One body per kind. The union is closed, so a kind that falls through to the pass-through text is a kind whose
// configuration an operator can never reach.
const bodyTestIdByKind: Record<GraphWorkflowNodeKind, string> = {
	Start: "gw-node-config-input-schema",
	Agent: "gw-node-config-instructions",
	Tool: "gw-node-config-tool",
	Condition: "gw-node-config-path",
	Parallel: "gw-node-config-passthrough",
	Join: "gw-node-config-passthrough",
	Pause: "gw-node-config-prompt",
	End: "gw-node-config-outcome",
};

describe("GraphWorkflowNodeConfigPanel", () => {
	it.each(graphWorkflowNodeKinds)("renders the %s body and the common header", (kind) => {
		renderPanel(defaultNodeData(kind, `${kind.toLowerCase()}-1`));

		expect((screen.getByTestId("gw-node-config-key") as HTMLInputElement).value).toBe(`${kind.toLowerCase()}-1`);
		expect(screen.getByTestId("gw-node-config-join")).toBeTruthy();
		expect(screen.getByTestId(bodyTestIdByKind[kind])).toBeTruthy();
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

	it("shows the kind's default max attempts and writes nothing on mount", () => {
		const onChange = vi.fn();
		renderPanel({ ...defaultNodeData("Agent", "agent-1"), maxAttempts: undefined } as GraphWorkflowCanvasNodeData, {
			onChange,
		});

		const attempts = screen.getByTestId("gw-node-config-attempts") as HTMLInputElement;
		expect(attempts.value).toBe("");
		// 3 for an Agent (F-1), shown as the placeholder because "unset" and "3" are different documents.
		expect(attempts.placeholder).toBe("3");
		expect(onChange).not.toHaveBeenCalled();

		fireEvent.change(attempts, { target: { value: "5" } });

		expect(onChange).toHaveBeenCalledWith({ maxAttempts: 5 });
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
});

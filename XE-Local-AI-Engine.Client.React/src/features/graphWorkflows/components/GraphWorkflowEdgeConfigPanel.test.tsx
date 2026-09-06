// @vitest-environment jsdom

// An edge's condition is the routing decision, so this file pins the three ways it can lie: an operand offered for an
// operator that takes none, an inherited Condition path rendered as if the edge had none, and a "conditional" switch
// turned off that leaves the condition behind to keep branching on the next save.

import { fireEvent, screen } from "@testing-library/react";
import { describe, expect, it, vi } from "vitest";

import { GraphWorkflowEdgeConfigPanel } from "@/features/graphWorkflows/components/GraphWorkflowEdgeConfigPanel";
import {
	type GraphWorkflowCanvasEdge,
	type GraphWorkflowCanvasEdgeCondition,
	type GraphWorkflowCanvasNodeData,
	defaultNodeData,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

function edgeWith(condition?: GraphWorkflowCanvasEdgeCondition): GraphWorkflowCanvasEdge {
	return {
		id: "e1",
		source: "condition-1",
		target: "end-1",
		data: { label: "true", ...(condition ? { condition } : {}) },
	};
}

const conditionNode = {
	...defaultNodeData("Condition", "condition-1"),
	path: "output.json.status",
} as GraphWorkflowCanvasNodeData;

function renderPanel(
	edge: GraphWorkflowCanvasEdge,
	options: { sourceNode?: GraphWorkflowCanvasNodeData; onChange?: (patch: Record<string, unknown>) => void } = {},
) {
	return renderWithProviders(
		<GraphWorkflowEdgeConfigPanel
			edge={edge}
			sourceNode={options.sourceNode}
			issues={[]}
			onChange={options.onChange ?? vi.fn()}
			onRemove={vi.fn()}
		/>,
	);
}

describe("GraphWorkflowEdgeConfigPanel", () => {
	it("renders the label and endpoints of an unconditional edge without a condition trio", () => {
		renderPanel(edgeWith());

		expect((screen.getByTestId("gw-edge-config-label") as HTMLInputElement).value).toBe("true");
		expect(screen.getByTestId("gw-edge-config-endpoints").textContent).toBe("condition-1 → end-1");
		expect(screen.queryByTestId("gw-edge-config-operator")).toBeNull();
	});

	it("hides the value field for an operator that takes no operand", () => {
		renderPanel(edgeWith({ op: "Exists", value: "" }));

		expect(screen.getByTestId("gw-edge-config-operator")).toBeTruthy();
		expect(screen.queryByTestId("gw-edge-config-value")).toBeNull();
	});

	it("keeps the value field for a comparing operator", () => {
		renderPanel(edgeWith({ op: "Eq", value: "ready" }));

		expect((screen.getByTestId("gw-edge-config-value") as HTMLInputElement).value).toBe("ready");
	});

	it("shows the path inherited from the source Condition node when the edge has none", () => {
		renderPanel(edgeWith({ op: "Eq", value: "true" }), { sourceNode: conditionNode });

		const path = screen.getByTestId("gw-edge-config-path") as HTMLInputElement;
		expect(path.value).toBe("");
		expect(path.placeholder).toBe("output.json.status");
		expect(screen.getByText("Inherits “output.json.status” from condition-1")).toBeTruthy();
	});

	it("stops naming an inherited path once the edge carries one of its own", () => {
		renderPanel(edgeWith({ path: "output.json.other", op: "Eq", value: "true" }), { sourceNode: conditionNode });

		expect(screen.queryByText("Inherits “output.json.status” from condition-1")).toBeNull();
	});

	it("clears the condition outright when the conditional switch is turned off", () => {
		const onChange = vi.fn();
		renderPanel(edgeWith({ path: "output.json.status", op: "Eq", value: "true" }), { onChange });

		fireEvent.click(screen.getByTestId("gw-edge-config-conditional"));

		expect(onChange).toHaveBeenCalledWith({ condition: undefined });
	});

	it("seeds an Eq condition when the conditional switch is turned on", () => {
		const onChange = vi.fn();
		renderPanel(edgeWith(), { onChange });

		fireEvent.click(screen.getByTestId("gw-edge-config-conditional"));

		expect(onChange).toHaveBeenCalledWith({ condition: { op: "Eq", value: "" } });
	});
});

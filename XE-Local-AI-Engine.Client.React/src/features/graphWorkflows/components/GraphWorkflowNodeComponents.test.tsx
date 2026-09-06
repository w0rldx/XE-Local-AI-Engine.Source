// @vitest-environment jsdom

// The handles ARE the contract here: `onConnect` reads a handle id and turns it into the edge's `sourceHandle`, label
// and condition, so a Condition card that stopped offering `true`/`false` would silently unroute every branch an
// operator drew. A real `<Handle>` needs React Flow's node context, which only exists inside a mounted flow, so it is
// mocked down to the two attributes under test.

import { cleanup, screen } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { GraphWorkflowNodeCard } from "@/features/graphWorkflows/components/GraphWorkflowNodeComponents";
import {
	type GraphWorkflowCanvasNodeData,
	defaultNodeData,
	graphWorkflowNodeTypeByKind,
} from "@/features/graphWorkflows/models/GraphWorkflowCanvasModels";
import type { GraphWorkflowNodeKind } from "@/features/graphWorkflows/models/GraphWorkflowModels";
import { renderWithProviders } from "@/test/RenderWithProviders";

interface HandleMockProps {
	readonly type: string;
	readonly id?: string;
	readonly position: string;
}

vi.mock("@xyflow/react", () => ({
	Position: { Top: "top", Bottom: "bottom", Left: "left", Right: "right" },
	Handle: ({ type, id, position }: HandleMockProps) => (
		<div data-testid={`handle-${type}-${id ?? "default"}`} data-position={position} />
	),
}));

/** A node's defaults with a few members overridden. The data type is a union, so one cast beats eight builders. */
function nodeData(
	kind: GraphWorkflowNodeKind,
	key: string,
	extra: Record<string, unknown> = {},
): GraphWorkflowCanvasNodeData {
	return { ...defaultNodeData(kind, key), ...extra } as GraphWorkflowCanvasNodeData;
}

function renderCard(data: GraphWorkflowCanvasNodeData, options: { readonly selected?: boolean } = {}) {
	const type = graphWorkflowNodeTypeByKind[data.kind];
	return renderWithProviders(
		<GraphWorkflowNodeCard
			id={data.key}
			type={type}
			data={data}
			selected={options.selected ?? false}
			dragging={false}
			draggable={false}
			selectable={true}
			deletable={true}
			isConnectable={true}
			positionAbsoluteX={0}
			positionAbsoluteY={0}
			zIndex={0}
		/>,
	);
}

describe("GraphWorkflowNodeCard handles", () => {
	afterEach(cleanup);

	it("gives a Condition node two source handles, ids true and false", () => {
		renderCard(nodeData("Condition", "check"));

		expect(screen.getByTestId("handle-source-true")).toBeTruthy();
		expect(screen.getByTestId("handle-source-false")).toBeTruthy();
		expect(screen.queryByTestId("handle-source-default")).toBeNull();
		expect(screen.getByTestId("handle-target-default")).toBeTruthy();
	});

	it("gives a Pause node one source handle per allowed decision", () => {
		renderCard(nodeData("Pause", "review", { allowedDecisions: ["Approve"] }));

		expect(screen.getByTestId("handle-source-Approve")).toBeTruthy();
		expect(screen.queryByTestId("handle-source-Reject")).toBeNull();

		cleanup();
		renderCard(nodeData("Pause", "review"));

		expect(screen.getByTestId("handle-source-Approve")).toBeTruthy();
		expect(screen.getByTestId("handle-source-Reject")).toBeTruthy();
	});

	it("gives Start no target handle and End no source handle", () => {
		renderCard(nodeData("Start", "start"));
		expect(screen.queryByTestId("handle-target-default")).toBeNull();
		expect(screen.getByTestId("handle-source-default")).toBeTruthy();

		cleanup();
		renderCard(nodeData("End", "done"));
		expect(screen.getByTestId("handle-target-default")).toBeTruthy();
		expect(screen.queryByTestId("handle-source-default")).toBeNull();
	});

	it("gives Agent, Tool, Parallel and Join one handle at each end", () => {
		for (const kind of ["Agent", "Tool", "Parallel", "Join"] as const) {
			cleanup();
			renderCard(nodeData(kind, kind.toLowerCase()));
			expect(screen.getByTestId("handle-target-default"), kind).toBeTruthy();
			expect(screen.getByTestId("handle-source-default"), kind).toBeTruthy();
		}
	});
});

describe("GraphWorkflowNodeCard body", () => {
	afterEach(cleanup);

	it("falls back to the kind label when the node has no label of its own, and always shows the key", () => {
		renderCard(nodeData("Agent", "agent-1"));

		const card = screen.getByTestId("graph-workflow-node-agent-1");
		expect(card.textContent).toContain("Agent");
		expect(card.textContent).toContain("agent-1");
		expect(card.getAttribute("data-kind")).toBe("Agent");
	});

	it("shows the Any join policy, and nothing when it is the default All", () => {
		renderCard(nodeData("Join", "merge", { joinPolicy: "Any" }));
		expect(screen.getByTestId("graph-workflow-node-join-merge").textContent).toBe("First branch wins");

		cleanup();
		renderCard(nodeData("Join", "merge"));
		expect(screen.queryByTestId("graph-workflow-node-join-merge")).toBeNull();
	});

	it("shows a status chip only when a run supplied one", () => {
		renderCard(nodeData("Agent", "analyze"));
		expect(screen.queryByTestId("graph-workflow-node-status-analyze")).toBeNull();

		cleanup();
		renderCard(nodeData("Agent", "analyze", { runState: { status: "Failed", attempt: 2, failureClass: "NodeFailed" } }));
		expect(screen.getByTestId("graph-workflow-node-status-analyze").textContent).toBe("Failed");
		expect(screen.getByTestId("graph-workflow-node-analyze").getAttribute("data-status")).toBe("Failed");
	});

	it("rings a node the canvas flagged with an issue", () => {
		renderCard(nodeData("Agent", "analyze"));
		expect(screen.getByTestId("graph-workflow-node-analyze").getAttribute("data-has-issue")).toBe("false");

		cleanup();
		renderCard(nodeData("Agent", "analyze", { hasIssue: true }));
		expect(screen.getByTestId("graph-workflow-node-analyze").getAttribute("data-has-issue")).toBe("true");
	});
});

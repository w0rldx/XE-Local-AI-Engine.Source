// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { WorkflowCanvas } from "@/features/preview/components/WorkflowCanvas";
import { graphToCanvas } from "@/features/preview/models/PreviewCanvasModels";
import type { PreviewWorkflowGraph } from "@/features/preview/models/PreviewWorkflowModels";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, defaultValue?: string) => defaultValue ?? _key }),
}));

// React Flow renders into a measured container; jsdom reports 0×0, which only suppresses the viewport (the
// toolbar — the unit under test — still renders). The node components subscribe to the run store, which is fine
// with no active run. The AgentNodeForm's queries are only mounted when an Agent node is selected, which these
// tests do not do.

const INVALID_GRAPH: PreviewWorkflowGraph = {
	startText: "hi",
	// Start → End with NO agent between them: the validator rejects it, so Execute must be disabled.
	nodes: [
		{ id: "start", kind: "Start" },
		{ id: "end", kind: "End" },
	],
	edges: [{ sourceId: "start", targetId: "end" }],
};

const VALID_GRAPH: PreviewWorkflowGraph = {
	startText: "hi",
	nodes: [
		{ id: "start", kind: "Start" },
		{ id: "agent", kind: "Agent", label: "A", model: "qwen3:8b", instructions: "Respond." },
		{ id: "end", kind: "End" },
	],
	edges: [
		{ sourceId: "start", targetId: "agent" },
		{ sourceId: "agent", targetId: "end" },
	],
};

// jsdom has no DataTransfer — hand-roll a minimal stub.
const makeDataTransfer = () => {
	const store: Record<string, string> = {};
	return {
		setData: (k: string, v: string) => {
			store[k] = v;
		},
		getData: (k: string) => store[k] ?? "",
		dropEffect: "",
		effectAllowed: "",
	} as unknown as DataTransfer;
};

function renderCanvas(graph: PreviewWorkflowGraph) {
	const { nodes, edges } = graphToCanvas(graph);
	return render(
		<MantineProvider>
			<WorkflowCanvas
				initialNodes={nodes}
				initialEdges={edges}
				initialStartText={graph.startText}
				runState={{ isRunning: false, isPaused: false }}
				isControlBusy={false}
				onExecute={vi.fn()}
				onCancel={vi.fn()}
				onContinue={vi.fn()}
				onGraphChange={vi.fn()}
			/>
		</MantineProvider>,
	);
}

// Mantine reads window.matchMedia and React Flow reads ResizeObserver; jsdom provides neither, so stub them
// (mirrors the scheduler form test's jsdom-environment mocks).
function installJsdomEnvironmentMocks(): void {
	Object.defineProperty(window, "matchMedia", {
		writable: true,
		value: vi.fn().mockImplementation((query: string) => ({
			matches: false,
			media: query,
			onchange: null,
			addEventListener: vi.fn(),
			removeEventListener: vi.fn(),
			dispatchEvent: vi.fn(),
		})),
	});
	Object.defineProperty(window, "ResizeObserver", {
		writable: true,
		value: class ResizeObserverMock {
			observe = vi.fn();
			unobserve = vi.fn();
			disconnect = vi.fn();
		},
	});
}

describe("WorkflowCanvas", () => {
	beforeEach(installJsdomEnvironmentMocks);
	afterEach(cleanup);

	it("disables Execute when the graph is invalid (no Agent between Start and End)", () => {
		renderCanvas(INVALID_GRAPH);
		const execute = screen.getByTestId("preview-execute") as HTMLButtonElement;
		expect(execute.disabled).toBe(true);
	});

	it("enables Execute when the graph is a valid linear Start → Agent → End chain", () => {
		renderCanvas(VALID_GRAPH);
		const execute = screen.getByTestId("preview-execute") as HTMLButtonElement;
		expect(execute.disabled).toBe(false);
	});

	// Test A: draggable wrapper is present and encodes the correct payload on dragStart.
	it("palette drag wrapper has draggable attribute and encodes kind+type on dragStart", () => {
		renderCanvas(INVALID_GRAPH);
		const dragWrapper = screen.getByTestId("preview-palette-drag-agent") as HTMLDivElement;
		expect(dragWrapper.draggable).toBe(true);

		const dt = makeDataTransfer();
		fireEvent.dragStart(dragWrapper, { dataTransfer: dt });

		const payload = JSON.parse(dt.getData("application/xeflow")) as { kind: string; type: string };
		expect(payload).toEqual({ kind: "Agent", type: "agent" });
	});

	// Test B: dropping an agent payload onto the canvas dropzone calls onGraphChange with an additional Agent node.
	// jsdom viewport is 0×0 so we assert on node count/kind, not geometry.
	it("drop on canvas creates a new Agent node and emits onGraphChange", () => {
		const onGraphChange = vi.fn();
		const { nodes, edges } = graphToCanvas(INVALID_GRAPH);
		render(
			<MantineProvider>
				<WorkflowCanvas
					initialNodes={nodes}
					initialEdges={edges}
					initialStartText={INVALID_GRAPH.startText}
					runState={{ isRunning: false, isPaused: false }}
					isControlBusy={false}
					onExecute={vi.fn()}
					onCancel={vi.fn()}
					onContinue={vi.fn()}
					onGraphChange={onGraphChange}
				/>
			</MantineProvider>,
		);

		const dropzone = screen.getByTestId("preview-canvas-dropzone");
		const dt = makeDataTransfer();
		dt.setData("application/xeflow", JSON.stringify({ kind: "Agent", type: "agent" }));

		fireEvent.dragOver(dropzone, { dataTransfer: dt });
		fireEvent.drop(dropzone, { dataTransfer: dt });

		// onGraphChange should have been called; the last call's graph should contain a new Agent node.
		expect(onGraphChange).toHaveBeenCalled();
		const lastCall = onGraphChange.mock.calls.at(-1);
		const lastGraph = lastCall?.[0] as { nodes: Array<{ kind: string }> };
		const agentNodes = lastGraph?.nodes.filter((n) => n.kind === "Agent") ?? [];
		expect(agentNodes.length).toBeGreaterThanOrEqual(1);
	});
});

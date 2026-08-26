// @vitest-environment jsdom

import { MantineProvider } from "@mantine/core";
import { cleanup, fireEvent, render, screen, waitFor } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { WorkflowCanvas } from "@/features/preview/components/WorkflowCanvas";
import { graphToCanvas } from "@/features/preview/models/PreviewCanvasModels";
import type { PreviewWorkflowGraph } from "@/features/preview/models/PreviewWorkflowModels";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({ t: (_key: string, defaultValue?: string) => defaultValue ?? _key }),
}));

// The config form pulls agent definitions and local models through the query layer; the layout tests below care only
// about WHERE it is rendered, so stub it out rather than standing up a QueryClient. The tests that never select a
// node are unaffected — the form is not mounted for them either way.
vi.mock("@/features/preview/components/AgentNodeForm", () => ({
	AgentNodeForm: () => <div data-testid="agent-node-form" />,
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

// useWindowDimensions reads window.innerWidth, so the layout branch is driven by setting it. jsdom defaults to 1024
// — the two-pane layout — which is what the tests above assume.
function setWindowWidth(width: number): void {
	Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: width });
	fireEvent(window, new Event("resize"));
}

// Renders a canvas whose Agent node starts selected, so the per-node config panel is mounted.
function renderCanvasWithSelectedAgent() {
	const { nodes, edges } = graphToCanvas(VALID_GRAPH);
	return render(
		<MantineProvider>
			<WorkflowCanvas
				initialNodes={nodes.map((node) => (node.data.kind === "Agent" ? { ...node, selected: true } : node))}
				initialEdges={edges}
				initialStartText={VALID_GRAPH.startText}
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

describe("WorkflowCanvas responsive layout", () => {
	beforeEach(installJsdomEnvironmentMocks);
	afterEach(() => {
		cleanup();
		setWindowWidth(1024);
	});

	it("keeps the config panel beside the canvas at the two-pane width", () => {
		setWindowWidth(1280);
		renderCanvasWithSelectedAgent();

		expect(screen.queryByTestId("preview-node-config")).not.toBeNull();
		expect(screen.queryByTestId("preview-node-config-drawer")).toBeNull();
	});

	// The regression this guards: the open/close effect used to depend on useDisclosure's HANDLERS OBJECT, which
	// Mantine rebuilds every render. That made the effect run on every render, so the re-render caused by a manual
	// close immediately re-opened the drawer and it could not be dismissed while an Agent block stayed selected.
	it("keeps the drawer closed after the operator dismisses it, and reopens it from the toolbar", async () => {
		setWindowWidth(390);
		renderCanvasWithSelectedAgent();

		await screen.findByTestId("preview-node-config-drawer");

		fireEvent.click(screen.getByRole("button", { name: "Close" }));

		// waitFor, not an immediate assertion: the Drawer unmounts through its exit transition. With the bug the
		// element never leaves, because the close's own re-render re-opened it.
		await waitFor(() => {
			expect(screen.queryByTestId("preview-node-config-drawer")).toBeNull();
		});

		// The selection is untouched, so the toolbar keeps offering the way back in.
		fireEvent.click(screen.getByTestId("preview-node-config-toggle"));
		expect(await screen.findByTestId("preview-node-config-drawer")).toBeTruthy();
	});

	it("moves the config panel into a drawer below the two-pane width", async () => {
		setWindowWidth(390);
		renderCanvasWithSelectedAgent();

		// findBy, not getBy: the Drawer mounts through a Mantine transition, so it lands a frame after the effect that
		// follows the selection opens it.
		const drawer = await screen.findByTestId("preview-node-config-drawer");
		expect(drawer.contains(screen.getByTestId("preview-node-config"))).toBe(true);
		// The canvas keeps the whole row to itself rather than sharing it with a 360px panel beside it.
		expect(screen.getByTestId("preview-canvas").parentElement?.contains(drawer)).toBe(false);
	});
});

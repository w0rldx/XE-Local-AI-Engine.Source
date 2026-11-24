// @vitest-environment jsdom

import { fireEvent } from "@testing-library/react";
import { ReactFlow } from "@xyflow/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { ArchitectureModelGraph } from "@/features/modelLab/components/ArchitectureModelGraph";
import { buildArchitectureHierarchy } from "@/features/modelLab/models/ModelLabHierarchy";
import { renderWithProviders } from "@/test/RenderWithProviders";

// The real React Flow and d3-zoom: these tests dispatch the DOM events d3-zoom listens for on the pane and read the
// viewport transform React Flow writes back. jsdom cannot scroll, so "the page scrolls" is asserted as d3-zoom never
// cancelling the touchmove; the browser's own touch-action handling is not exercised here.

const hierarchy = buildArchitectureHierarchy({
	support: "detailed",
	blocks: [],
	nonBlock: [
		{
			componentId: "embedding",
			component: "embedding",
			provenance: "read",
			tensors: [
				{ ggufName: "embedding.weight", hfName: null, shape: [64], ggmlType: "F32", byteSize: null, valueRelation: null },
			],
		},
	],
	hyperparameters: null,
});

function renderGraph() {
	if (hierarchy === null) {
		throw new Error("a readable file builds a hierarchy");
	}
	return renderWithProviders(
		<ArchitectureModelGraph
			hierarchy={hierarchy}
			selection={null}
			tracedBlocks={null}
			routedBlocks={null}
			templates={[]}
			onSelect={vi.fn()}
		/>,
	);
}

function pane(container: HTMLElement): Element {
	const found = container.querySelector(".react-flow__pane");
	if (found === null) {
		throw new Error("React Flow renders its pane");
	}
	return found;
}

function viewportTransform(container: HTMLElement): string {
	return (container.querySelector(".react-flow__viewport") as HTMLElement | null)?.style.transform ?? "";
}

// A one-finger vertical swipe; returns the touchmove, whose defaultPrevented says whether d3-zoom took the gesture.
function swipe(target: Element): Event {
	const touchAt = (clientY: number) => ({ identifier: 0, clientX: 40, clientY, pageX: 40, pageY: clientY });
	const touchEvent = (type: string, clientY: number) => {
		const event = new Event(type, { bubbles: true, cancelable: true });
		const touches = type === "touchend" ? [] : [touchAt(clientY)];
		return Object.assign(event, { touches, changedTouches: [touchAt(clientY)], targetTouches: touches });
	};
	fireEvent(target, touchEvent("touchstart", 100));
	const move = touchEvent("touchmove", 40);
	fireEvent(target, move);
	fireEvent(target, touchEvent("touchend", 40));
	return move;
}

describe("ArchitectureModelGraph gestures", () => {
	// d3-zoom attaches its touch listeners only on a touch-capable device, judged when the zoom is attached.
	beforeEach(() => {
		Object.defineProperty(navigator, "maxTouchPoints", { configurable: true, value: 1 });
	});
	afterEach(() => {
		Reflect.deleteProperty(navigator, "maxTouchPoints");
	});

	it("pans the graph on a mouse drag", () => {
		const { container } = renderGraph();
		const before = viewportTransform(container);

		// d3-zoom follows the drag on the event's view; jsdom's MouseEvent rejects the test's window as `view`, so it is set after.
		const mouse = (type: string, clientX: number, clientY: number) =>
			Object.defineProperty(new MouseEvent(type, { bubbles: true, cancelable: true, clientX, clientY }), "view", {
				value: window,
			});
		fireEvent(pane(container), mouse("mousedown", 10, 10));
		fireEvent(window, mouse("mousemove", 40, 50));
		fireEvent(window, mouse("mouseup", 40, 50));

		expect(before).toBe("translate(0px,0px) scale(1)");
		expect(viewportTransform(container)).toBe("translate(30px,40px) scale(1)");
	});

	it("leaves a one-finger swipe to the page", () => {
		const { container } = renderGraph();
		const before = viewportTransform(container);

		const move = swipe(pane(container));

		expect(move.defaultPrevented).toBe(false);
		expect(viewportTransform(container)).toBe(before);
	});

	// The control: the same swipe on a bare React Flow that pans on drag is taken by d3-zoom, so the test above would
	// see a cancelled touchmove and a moved viewport if the graph let the touch through.
	it("control: a bare React Flow with panOnDrag takes the same swipe", () => {
		const { container } = renderWithProviders(
			<div style={{ height: 200 }}>
				<ReactFlow
					nodes={[{ id: "a", position: { x: 0, y: 0 }, data: { label: "a" } }]}
					edges={[]}
					panOnDrag={true}
					zoomOnScroll={false}
					preventScrolling={false}
				/>
			</div>,
		);
		const before = viewportTransform(container);

		const move = swipe(pane(container));

		expect(move.defaultPrevented).toBe(true);
		expect(viewportTransform(container)).not.toBe(before);
	});
});

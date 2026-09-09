// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { ResponsivePaneLayout } from "@/core/ui/components/ResponsivePaneLayout/ResponsivePaneLayout";
import { renderWithProviders } from "@/test/RenderWithProviders";

function setViewportWidth(width: number): void {
	Object.defineProperty(window, "innerWidth", { writable: true, configurable: true, value: width });
}

const panes = {
	list: <div data-testid="pane-list">list</div>,
	main: <div data-testid="pane-main">main</div>,
	side: <div data-testid="pane-side">side</div>,
};

describe("ResponsivePaneLayout", () => {
	beforeEach(() => {
		// jsdom's default and TWO_PANE_BREAKPOINT itself: the wide layout is the default under test.
		setViewportWidth(1024);
	});

	afterEach(() => {
		cleanup();
	});

	it("renders three floored columns at or above the two-pane breakpoint", () => {
		renderWithProviders(<ResponsivePaneLayout narrowMode="mainOnly" {...panes} narrowTestId="narrow" gridTestId="grid" />);

		const grid = screen.getByTestId("grid");
		// The floors are the documented fix for the centre track collapsing under its own chrome just above 1024,
		// and `overflowX: auto` is what makes the overflow they cause scrollable instead of clipped.
		expect(grid.style.gridTemplateColumns).toBe("320px minmax(240px, 1fr) minmax(380px, 420px)");
		expect(grid.style.overflowX).toBe("auto");
		expect(screen.getByTestId("pane-list")).toBeDefined();
		expect(screen.getByTestId("pane-main")).toBeDefined();
		expect(screen.getByTestId("pane-side")).toBeDefined();
		expect(screen.queryByTestId("narrow")).toBeNull();
	});

	it("drops the third column, and its track, when there is no side pane", () => {
		renderWithProviders(<ResponsivePaneLayout narrowMode="mainOnly" list={panes.list} main={panes.main} gridTestId="grid" />);

		expect(screen.getByTestId("grid").style.gridTemplateColumns).toBe("320px minmax(240px, 1fr)");
		expect(screen.queryByTestId("pane-side")).toBeNull();
	});

	it("keeps the list on screen above the main pane in the stacking narrow mode", () => {
		setViewportWidth(800);
		renderWithProviders(<ResponsivePaneLayout narrowMode="stack" {...panes} narrowTestId="narrow" gridTestId="grid" />);

		expect(screen.queryByTestId("grid")).toBeNull();
		expect(screen.getByTestId("narrow")).toBeDefined();
		expect(screen.getByTestId("pane-list")).toBeDefined();
		expect(screen.getByTestId("pane-main")).toBeDefined();
		// Never rendered twice: the side surface belongs to the call site's Drawer below the breakpoint.
		expect(screen.queryByTestId("pane-side")).toBeNull();
	});

	it("renders only the main pane in the drawer-backed narrow mode", () => {
		setViewportWidth(800);
		renderWithProviders(<ResponsivePaneLayout narrowMode="mainOnly" {...panes} narrowTestId="narrow" gridTestId="grid" />);

		expect(screen.queryByTestId("grid")).toBeNull();
		expect(screen.getByTestId("pane-main")).toBeDefined();
		// The page opens these from its header Drawers; rendering them here too would duplicate every testid.
		expect(screen.queryByTestId("pane-list")).toBeNull();
		expect(screen.queryByTestId("pane-side")).toBeNull();
	});
});

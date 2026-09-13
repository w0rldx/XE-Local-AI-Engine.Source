// @vitest-environment jsdom

import { cleanup, render, screen } from "@testing-library/react";
import { useEffect } from "react";
import { afterEach, describe, expect, it } from "vitest";

import { ResponsivePaneLayout } from "@/core/ui/components/ResponsivePaneLayout/ResponsivePaneLayout";
import { renderWithProviders } from "@/test/RenderWithProviders";

const panes = {
	list: <div data-testid="pane-list">list</div>,
	main: <div data-testid="pane-main">main</div>,
	side: <div data-testid="pane-side">side</div>,
};

// The component no longer measures anything: the page decides once (usePaneLayoutMode) and hands the answer down,
// so these drive the prop rather than a viewport that the component would now ignore.
describe("ResponsivePaneLayout", () => {
	afterEach(() => {
		cleanup();
	});

	it("renders three floored columns when the page says the container has room", () => {
		renderWithProviders(
			<ResponsivePaneLayout isNarrow={false} narrowMode="mainOnly" {...panes} narrowTestId="narrow" gridTestId="grid" />,
		);

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
		renderWithProviders(
			<ResponsivePaneLayout isNarrow={false} narrowMode="mainOnly" list={panes.list} main={panes.main} gridTestId="grid" />,
		);

		expect(screen.getByTestId("grid").style.gridTemplateColumns).toBe("320px minmax(240px, 1fr)");
		expect(screen.queryByTestId("pane-side")).toBeNull();
	});

	it("keeps the list on screen above the main pane in the stacking narrow mode", () => {
		renderWithProviders(
			<ResponsivePaneLayout isNarrow={true} narrowMode="stack" {...panes} narrowTestId="narrow" gridTestId="grid" />,
		);

		expect(screen.queryByTestId("grid")).toBeNull();
		expect(screen.getByTestId("narrow")).toBeDefined();
		expect(screen.getByTestId("pane-list")).toBeDefined();
		expect(screen.getByTestId("pane-main")).toBeDefined();
		// Never rendered twice: the side surface belongs to the call site's Drawer below the breakpoint.
		expect(screen.queryByTestId("pane-side")).toBeNull();
	});

	it("renders only the main pane in the drawer-backed narrow mode", () => {
		renderWithProviders(
			<ResponsivePaneLayout isNarrow={true} narrowMode="mainOnly" {...panes} narrowTestId="narrow" gridTestId="grid" />,
		);

		expect(screen.queryByTestId("grid")).toBeNull();
		expect(screen.getByTestId("pane-main")).toBeDefined();
		// The page opens these from its header Drawers; rendering them here too would duplicate every testid.
		expect(screen.queryByTestId("pane-list")).toBeNull();
		expect(screen.queryByTestId("pane-side")).toBeNull();
	});

	// The regression this component's single root exists to prevent: `usePaneLayoutMode` guesses from the viewport on
	// the first render and corrects itself from the measured container in a layout effect, so `isNarrow` flips under a
	// mounted subtree as a matter of course. `main` is `<Chat>` on the work-session page — rebuilding it re-runs its
	// mount effects. Two JSX shapes did exactly that; one keyed root reconciles instead.
	describe.each(["stack", "mainOnly"] as const)("does not remount the main pane across a mode flip (%s)", (narrowMode) => {
		function CountingMain({ onMount, onUnmount }: { readonly onMount: () => void; readonly onUnmount: () => void }) {
			useEffect(() => {
				onMount();
				return onUnmount;
			}, [onMount, onUnmount]);
			return <div data-testid="pane-main">main</div>;
		}

		it.each([
			{ label: "narrow to wide", first: true },
			{ label: "wide to narrow", first: false },
		])("$label", ({ first }) => {
			let mounts = 0;
			let unmounts = 0;
			const main = (
				<CountingMain
					onMount={() => {
						mounts += 1;
					}}
					onUnmount={() => {
						unmounts += 1;
					}}
				/>
			);
			const layout = (isNarrow: boolean) => (
				<ResponsivePaneLayout
					isNarrow={isNarrow}
					narrowMode={narrowMode}
					list={panes.list}
					main={main}
					side={panes.side}
					narrowTestId="narrow"
					gridTestId="grid"
				/>
			);

			// Testing Library's `rerender` replaces the root element, so a manually composed provider stack would be
			// dropped by it and every child would remount for that reason alone. This component needs no providers.
			const { container, rerender } = render(layout(first));
			const node = container.querySelector('[data-testid="pane-main"]');
			expect(mounts).toBe(1);
			expect(unmounts).toBe(0);
			expect(node).not.toBeNull();

			rerender(layout(!first));
			expect(mounts).toBe(1);
			expect(unmounts).toBe(0);
			// Identity, not just presence: a rebuilt pane would be a different element with the same testid.
			expect(container.querySelector('[data-testid="pane-main"]')).toBe(node);

			rerender(layout(first));
			expect(mounts).toBe(1);
			expect(unmounts).toBe(0);
			expect(container.querySelector('[data-testid="pane-main"]')).toBe(node);
		});
	});
});

import { Stack } from "@mantine/core";
import type { ReactNode } from "react";

import { TWO_PANE_BREAKPOINT } from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";

export interface ResponsivePaneLayoutProps {
	/**
	 * What the narrow viewport does with `list`. `"stack"` keeps it on screen above `main` — for a page where the list
	 * is the only way to reach a sibling record. `"mainOnly"` drops it, for a page that offers it through a Drawer of
	 * its own opened from the page header.
	 */
	readonly narrowMode: "stack" | "mainOnly";
	/** The left rail: a definition list, a run list, a plan panel. The first grid column. */
	readonly list: ReactNode;
	/** The centre pane. The only pane a narrow viewport is guaranteed to show. */
	readonly main: ReactNode;
	/** The third column, decoration included (a `Paper` wrap, a scroller). Falsy keeps the grid at two columns. */
	readonly side?: ReactNode;
	readonly narrowTestId?: string;
	readonly gridTestId: string;
}

/**
 * The narrow/wide switch and the wide three-column grid shared by the graph-workflow editor and run modes, the
 * development-workflow detail page and the work-session detail page. Four call sites had rebuilt the same grid, and
 * the graph-workflow copy had silently drifted to a different centre floor; one component is what stops a fifth
 * variant appearing and what makes the floors below a single decision rather than four.
 *
 * This is layout only — WHAT is in a pane stays in the feature that owns it, which is why every pane arrives as a
 * node and the Drawers that carry a pane on a narrow viewport stay at the call site: their position, size, title and
 * open-state are feature state, and a prop object for each would share the word `Drawer` and nothing else.
 * (`docs/wiki/22-workflow-engines-divergence-register.md` D2/D10 keep the two workflow engines' *behaviour* apart;
 * the owner has ruled that this viewport arithmetic is not engine behaviour and is shared deliberately.)
 *
 * The centre track carries a floor. `TWO_PANE_BREAKPOINT` asks "do two panes fit at all", not "does 320 + 380 plus
 * this page's chrome fit", so with `minmax(0, 1fr)` a viewport just above it left the centre at ~120px and clipped a
 * tab header to "Gra"/"Nod". `overflowX: auto` is the other half: FullHeightPage clips the X axis on purpose, so
 * without its own scroller the grid would go on hiding the overflow the floor makes honest.
 */
export function ResponsivePaneLayout({ narrowMode, list, main, side, narrowTestId, gridTestId }: ResponsivePaneLayoutProps) {
	const { width } = useWindowDimensions();

	if (width < TWO_PANE_BREAKPOINT) {
		return narrowMode === "stack" ? (
			<Stack gap="sm" style={{ flex: 1, minHeight: 0 }} data-testid={narrowTestId}>
				{list}
				<div style={{ flex: 1, minHeight: 0 }}>{main}</div>
			</Stack>
		) : (
			<div style={{ flex: 1, minHeight: 0 }} data-testid={narrowTestId}>
				{main}
			</div>
		);
	}

	return (
		<div
			data-testid={gridTestId}
			style={{
				display: "grid",
				gridTemplateColumns: side ? "320px minmax(240px, 1fr) minmax(380px, 420px)" : "320px minmax(240px, 1fr)",
				gridTemplateRows: "minmax(0, 1fr)",
				gap: "var(--mantine-spacing-md)",
				flex: 1,
				minHeight: 0,
				overflowX: "auto",
			}}
		>
			{list}
			{main}
			{side}
		</div>
	);
}

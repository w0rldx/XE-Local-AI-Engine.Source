import type { CSSProperties, ReactNode } from "react";

import { THREE_COLUMN_TRACKS, TWO_COLUMN_TRACKS } from "@/core/ui/components/ResponsivePaneLayout/PaneLayoutTracks";

export interface ResponsivePaneLayoutProps {
	/**
	 * Whether the panes must stack instead of sitting side by side. The page decides it once, with
	 * `usePaneLayoutMode`, and hands the same boolean to its Drawers, its header toggles and here.
	 */
	readonly isNarrow: boolean;
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

/** Keeps a reconciliation wrapper out of layout: its child stays the grid item / flex child it was. */
const CONTENTS: CSSProperties = { display: "contents" };
const FILL: CSSProperties = { flex: 1, minHeight: 0 };

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
 * the owner has ruled that this arithmetic is not engine behaviour and is shared deliberately.)
 *
 * `isNarrow` is a prop rather than a viewport read of this component's own. The decision belongs to the page and is
 * made ONCE there, because the page's Drawers, its header toggles and this grid must agree exactly — and because the
 * viewport is the wrong thing to measure: this grid lives inside the app shell, beside a sidebar whose width changes
 * with no resize event at all. `usePaneLayoutMode` measures the container instead. That one decision covers the page's
 * own surfaces; what a pane does INSIDE itself is its own affair, and the embedded chat still sizes its composer and
 * its message list from the viewport.
 *
 * Both modes render ONE root element carrying the same three keyed wrappers in the same order, because `main` is a
 * live subtree — on the work-session page it is `<Chat>`, with mount effects. A first render whose viewport guess
 * loses to the measured container flips `isNarrow` in a layout effect; two different JSX shapes would make React
 * destroy and rebuild `main` right there, and again on every resize across the threshold and every sidebar collapse.
 * The wrappers exist only so React can reconcile the panes: `display: contents` keeps them out of layout entirely, so
 * the panes stay the grid's own items when wide, and the list stays a direct child of the stack when narrow.
 *
 * The centre track carries a floor: with `minmax(0, 1fr)` a container just wide enough for two panes left the centre
 * at ~120px and clipped a tab header to "Gra"/"Nod". `overflowX: auto` is the other half: FullHeightPage clips the X
 * axis on purpose, so without its own scroller the grid would go on hiding the overflow the floor makes honest.
 */
export function ResponsivePaneLayout({
	isNarrow,
	narrowMode,
	list,
	main,
	side,
	narrowTestId,
	gridTestId,
}: ResponsivePaneLayoutProps) {
	const showList = !isNarrow || narrowMode === "stack";

	return (
		<div
			data-testid={isNarrow ? narrowTestId : gridTestId}
			style={
				isNarrow
					? { display: "flex", flexDirection: "column", gap: "var(--mantine-spacing-sm)", ...FILL }
					: {
							display: "grid",
							gridTemplateColumns: side ? THREE_COLUMN_TRACKS : TWO_COLUMN_TRACKS,
							gridTemplateRows: "minmax(0, 1fr)",
							gap: "var(--mantine-spacing-md)",
							overflowX: "auto",
							...FILL,
						}
			}
		>
			{showList ? (
				<div key="list" style={CONTENTS}>
					{list}
				</div>
			) : null}
			<div key="main" style={isNarrow ? FILL : CONTENTS}>
				{main}
			</div>
			{!isNarrow && side ? (
				<div key="side" style={CONTENTS}>
					{side}
				</div>
			) : null}
		</div>
	);
}

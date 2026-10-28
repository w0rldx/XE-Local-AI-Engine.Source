/**
 * The wide pane grid's track sizes, and the container width they add up to.
 *
 * Their own module because `ResponsivePaneLayout.tsx` may only export components (Fast Refresh), and because the
 * threshold and the tracks must come from ONE place: a change to a floor that left the threshold behind would put the
 * grid back to overflowing its container, which is the defect this pair exists to prevent.
 */

/** The list rail's fixed track. */
const PANE_LIST_WIDTH = 320;
/** The center track's floor — below this a tab header clipped to "Gra"/"Nod". */
const PANE_MAIN_MIN_WIDTH = 240;
/** The side track's floor and ceiling. */
const PANE_SIDE_MIN_WIDTH = 380;
const PANE_SIDE_MAX_WIDTH = 420;
/** Mirrors the grid's `gap`: `var(--mantine-spacing-md)` is 16px. Two gaps sit between three columns. */
const PANE_GAP = 16;

/**
 * The narrowest container width (px) the three-column grid fits in without scrolling: 972 with the shared center
 * floor.
 *
 * It is always the THREE-column minimum, even on a page whose `side` pane is currently absent: the side pane comes and
 * goes with a selection at every call site, so a threshold that followed it would restack the whole page the moment
 * the user selects a node and unstack it when they clear the selection.
 *
 * `mainMinWidth` is for a page whose center needs more than the shared floor: a work session's center pane is a whole
 * chat, which at 240px stacked its composer controls four rows deep. The page then stays narrow until the center gets
 * that much; the tracks keep the shared floor, so wide mode only ever has more room. Anything below the shared floor
 * (0 for "no preference") counts as the shared floor.
 */
export function widePaneMinWidth(mainMinWidth: number): number {
	return PANE_LIST_WIDTH + Math.max(mainMinWidth, PANE_MAIN_MIN_WIDTH) + PANE_SIDE_MIN_WIDTH + 2 * PANE_GAP;
}

export const THREE_COLUMN_TRACKS = `${PANE_LIST_WIDTH}px minmax(${PANE_MAIN_MIN_WIDTH}px, 1fr) minmax(${PANE_SIDE_MIN_WIDTH}px, ${PANE_SIDE_MAX_WIDTH}px)`;
export const TWO_COLUMN_TRACKS = `${PANE_LIST_WIDTH}px minmax(${PANE_MAIN_MIN_WIDTH}px, 1fr)`;

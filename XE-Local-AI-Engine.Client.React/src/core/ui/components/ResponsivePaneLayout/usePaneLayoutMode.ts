import { type Ref, useLayoutEffect, useState } from "react";

import { TWO_PANE_BREAKPOINT } from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { WIDE_PANE_MIN_WIDTH } from "@/core/ui/components/ResponsivePaneLayout/PaneLayoutTracks";

export interface PaneLayoutMode {
	/**
	 * Attach to the page's own outer content wrapper — the element whose width IS the space the panes get. It must
	 * exist in both branches, so the pane grid itself is not a valid target (it does not render when narrow).
	 */
	readonly ref: Ref<HTMLDivElement>;
	/**
	 * True when the measured container cannot hold the three-column grid. One decision for everything the PAGE owns:
	 * its Drawers, its header toggles and the pane grid. A pane's own internals are not bound by it.
	 */
	readonly isNarrow: boolean;
}

/**
 * The single narrow/wide decision for a page that uses `ResponsivePaneLayout`: the page's Drawers, its header toggles
 * and the grid all read this one boolean. It governs the page's own surfaces only — a pane is free to stay responsive
 * inside itself, and the chat embedded on the work-session page still sizes its composer and message list from the
 * viewport rather than from this.
 *
 * It measures the CONTAINER, not the viewport, because the pane grid sits inside the app shell: a 220px (or 56px
 * collapsed) sidebar plus the content padding stand between the window and the panes. Measured live, a 1024px viewport
 * gave the grid 725px of room for 972px of tracks, so the third column was reachable only by scrolling sideways. The
 * sidebar also collapses with no window resize at all, which a viewport read can never notice.
 *
 * This is a deliberate behaviour change: with the sidebar expanded a page now goes narrow below roughly a 1256px
 * viewport rather than 1024px, and it goes wide again when the sidebar is collapsed and the space allows.
 *
 * The viewport rule survives as the fallback for an UNMEASURED container — before the ref is attached, and in jsdom,
 * where every element measures 0. The first measurement is taken synchronously in a layout effect so the first paint
 * is already correct; `@mantine/hooks`'s `useElementSize` reports 0 until a debounced animation frame and would
 * reintroduce the narrow→wide flash this page family was built to avoid.
 */
export function usePaneLayoutMode(): PaneLayoutMode {
	const [container, setContainer] = useState<HTMLDivElement | null>(null);
	const [containerWidth, setContainerWidth] = useState(0);
	// Only consulted while the container is unmeasured, but the subscription has to exist unconditionally so a resize
	// in that state still re-renders.
	const { width: viewportWidth } = useWindowDimensions();

	useLayoutEffect(() => {
		if (container === null) {
			setContainerWidth(0);
			return;
		}

		// The CONTENT box, because `WIDE_PANE_MIN_WIDTH` is the width of the tracks themselves: `clientWidth` is the
		// padding box, so the moment any of these frames gains horizontal padding a padding-box reading would claim
		// room the grid never gets and let it overflow again — the exact defect `PaneLayoutTracks` exists to prevent.
		// Erring narrow is safe, erring wide is not. One `measure()` for the first reading and the observer so the two
		// can never disagree; the observer's own `contentRect` is deliberately unused for the same reason.
		const measure = () => {
			const { paddingLeft, paddingRight } = getComputedStyle(container);
			// jsdom returns "" for an unset padding, and parseFloat("") is NaN.
			setContainerWidth(container.clientWidth - (Number.parseFloat(paddingLeft) || 0) - (Number.parseFloat(paddingRight) || 0));
		};

		measure();
		const observer = new ResizeObserver(measure);
		observer.observe(container);
		return () => observer.disconnect();
	}, [container]);

	return {
		ref: setContainer,
		isNarrow: containerWidth > 0 ? containerWidth < WIDE_PANE_MIN_WIDTH : viewportWidth < TWO_PANE_BREAKPOINT,
	};
}

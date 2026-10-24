import { COMPACT_CONTROLS_BREAKPOINT, isDesktopShell } from "@/core/layout/constants/LayoutBreakpoints";

/**
 * Cards when the table would not fit its container, but never in the desktop shell: there the sidebar is up and a
 * dense table that scrolls reads better than a long column of cards. Until the container has been measured (width 0
 * on first paint) the viewport alone decides, with cards below `sm`.
 */
export function shouldRenderCards(
	viewportWidth: number,
	viewportHeight: number,
	containerWidth: number,
	minWidth: number,
): boolean {
	if (isDesktopShell(viewportWidth, viewportHeight)) {
		return false;
	}
	return containerWidth > 0 ? containerWidth < minWidth : viewportWidth < COMPACT_CONTROLS_BREAKPOINT;
}

// Single source of truth for the layout breakpoints and sidebar widths that the app resolves in JS.
//
// These exist because a handful of layout decisions cannot be expressed as CSS media queries: the shell's committed
// margin and width are animated through Framer Motion layout projection, the shell switch needs width AND height, and
// DialogShell, ChatDisplayShell and WorkSessionDetailPage branch on the viewport to swap whole subtrees. Before this
// module each site carried its own literal, so the shell, dialogs and two-pane pages could drift apart silently.
//
// The values are plain literals rather than a read of `sourceThemeConfiguration`, on purpose:
//   * the same numbers are also baked into CSS files, which cannot follow a runtime theme override, so a runtime read
//     would let JS and CSS disagree;
//   * a user-supplied theme.json may retune the Mantine breakpoints for spacing without wanting the app shell to
//     restructure itself.
// LayoutBreakpoints.test.ts asserts they still match the theme's sm/lg, so a deliberate theme change fails loudly
// instead of drifting.

/**
 * Width (px) below which a dense control row drops its secondary text and keeps only what identifies each control.
 * Theme `sm`. Today: the chat composer's model picker, which shows the model name over its size/connection line and
 * below this width shows the name alone, so the row still fits beside the send button on a phone.
 */
export const COMPACT_CONTROLS_BREAKPOINT = 640;

/**
 * Width (px) at or above which the persistent desktop navigation sidebar replaces the mobile navigation. Theme `lg`.
 * At `md` (768) the 220px sidebar left a portrait tablet ~548px of content and pushed the header's Logout off-screen.
 */
export const DESKTOP_NAV_BREAKPOINT = 1024;

/**
 * Height (px) the viewport needs, together with {@link DESKTOP_NAV_BREAKPOINT}, for the desktop shell. Phone landscape
 * tops out around 430px tall, while the shortest laptop and tablet-landscape windows (1024×600 netbooks, 1280×720 with
 * browser chrome) stay above ~550px, so 520 sends every phone to the mobile shell without dropping a real desktop.
 */
export const DESKTOP_NAV_MIN_HEIGHT = 520;

/**
 * Width (px) at or above which a page may show two side-by-side panes. Theme `lg`, the same width the desktop sidebar
 * starts at, so a two-pane page sits beside the sidebar; below it the secondary pane moves into an off-canvas Drawer
 * (chat conversation list, work-session plan/side panel).
 */
export const TWO_PANE_BREAKPOINT = 1024;

/** Width (px) of the desktop navigation sidebar when expanded. */
export const SIDEBAR_WIDTH_EXPANDED = 220;

/** Width (px) of the desktop navigation sidebar when collapsed to icons only. */
export const SIDEBAR_WIDTH_COLLAPSED = 56;

/**
 * The one shell switch: the persistent sidebar and the desktop header need a viewport that is wide AND tall enough.
 * Everything else gets the mobile header and drawer. Layout, HeaderBar, DialogShell and RuntimeResourcesWidget all
 * branch on this, so the shell and its dependents cannot disagree.
 */
export function isDesktopShell(width: number, height: number): boolean {
	return width >= DESKTOP_NAV_BREAKPOINT && height >= DESKTOP_NAV_MIN_HEIGHT;
}

/**
 * A viewport where a page must spend as little height and width as possible on chrome: phone width, or any viewport
 * too short for the desktop shell (phone landscape). The chat composer folds its secondary controls and node settings
 * swaps its section list for a select.
 */
export function isCompactViewport(width: number, height: number): boolean {
	return width < COMPACT_CONTROLS_BREAKPOINT || height < DESKTOP_NAV_MIN_HEIGHT;
}

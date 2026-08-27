// Single source of truth for the layout breakpoints and sidebar widths that the app resolves in JS.
//
// These exist because a handful of layout decisions cannot be expressed as CSS media queries: the shell's committed
// margin and width are animated through Framer Motion layout projection, while DialogShell, ChatDisplayShell and
// WorkSessionDetailPage branch on the viewport to swap whole subtrees. Before this module each site carried its own
// literal, so the shell, dialogs and two-pane pages could drift apart silently.
//
// The values are plain literals rather than a read of `sourceThemeConfiguration`, on purpose:
//   * the same numbers are also baked into static UnoCSS classes (`hidden md:block` in Layout) and into CSS files,
//     which cannot follow a runtime theme override, so a runtime read would let JS and CSS disagree;
//   * a user-supplied theme.json may retune the Mantine breakpoints for spacing without wanting the app shell to
//     restructure itself.
// LayoutBreakpoints.test.ts asserts they still match the theme's md/lg, so a deliberate theme change fails loudly
// instead of drifting.

/**
 * Width (px) below which a dense control row drops its secondary text and keeps only what identifies each control.
 * Theme `sm`. Today: the chat composer's model picker, which shows the model name over its size/connection line and
 * below this width shows the name alone, so the row still fits beside the send button on a phone.
 */
export const COMPACT_CONTROLS_BREAKPOINT = 640;

/** Width (px) at or above which the persistent desktop navigation sidebar replaces the mobile navigation. Theme `md`. */
export const DESKTOP_NAV_BREAKPOINT = 768;

/**
 * Width (px) at or above which a page may show two side-by-side panes next to the shell sidebar. Theme `lg`.
 * Below it, the secondary pane moves into an off-canvas Drawer (chat conversation list, work-session plan/side panel).
 */
export const TWO_PANE_BREAKPOINT = 1024;

/** Width (px) of the desktop navigation sidebar when expanded. */
export const SIDEBAR_WIDTH_EXPANDED = 220;

/** Width (px) of the desktop navigation sidebar when collapsed to icons only. */
export const SIDEBAR_WIDTH_COLLAPSED = 56;

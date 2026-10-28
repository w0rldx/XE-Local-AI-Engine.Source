import "./Layout.css";

import { CatchBoundary, Outlet, useRouterState } from "@tanstack/react-router";
import { m } from "framer-motion";
import { lazy, type ReactNode, Suspense } from "react";
import { useTranslation } from "react-i18next";

import { RootErrorComponent } from "@/core/integrations/tanstack-router/RootErrorComponent";
import { DesktopNavigationBar } from "@/core/layout/components/DesktopNavigationBar/DesktopNavigationBar";
import { HeaderBar, type IHeaderBarProperties } from "@/core/layout/components/HeaderBar/HeaderBar";
import { isDesktopShell, SIDEBAR_WIDTH_COLLAPSED, SIDEBAR_WIDTH_EXPANDED } from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { useDesktopNavigationBarStore } from "@/core/layout/stores/DesktopNavigationBarStore";

const DevelopmentUi = import.meta.env.DEV
	? lazy(() => import("@/core/dev-tools/components/DevelopmentUi/DevelopmentUi").then((m) => ({ default: m.DevelopmentUi })))
	: null;

// A page crash must not take the navigation with it: without this boundary the root route's error component replaces
// the whole shell, and on the desktop WebView (no back button, no URL bar) Retry re-renders the same crash. It sits
// around the Outlet, not on the `/_layout` route, because a route's error boundary wraps that route's own component.
// Keyed on the pathname so navigating away clears the error; only this small component re-renders on navigation.
function RoutedPage() {
	const pathname = useRouterState({ select: (state) => state.location.pathname });
	return (
		<CatchBoundary getResetKey={() => pathname} errorComponent={RootErrorComponent}>
			<Outlet />
		</CatchBoundary>
	);
}

export interface ILayoutProperties extends IHeaderBarProperties {
	/** Rendered under the header bar on every page, above the routed content. */
	banners: ReactNode;
	/** Rendered after the shell, for floating elements such as status chips and dialogs. */
	overlay: ReactNode;
}

export function Layout({ headerStatus, headerActions, drawerActions, banners, overlay }: ILayoutProperties) {
	const { t } = useTranslation();
	const sideBarCollapsed = useDesktopNavigationBarStore((state) => state.sidebarState);
	const setSideBarCollapsed = useDesktopNavigationBarStore((state) => state.actions.setSidebarState);
	// The desktop breakpoint is resolved in JS (not a CSS media query) because the
	// resulting marginLeft/width define the committed layout that Framer Motion's layout
	// projection animates with compositor transforms. Tradeoff: on first paint before hydration
	// `width` reflects the real window, so there is no flash in practice, but SSR/prerender
	// would briefly render the mobile (100%) layout. Keeping it here avoids duplicating the
	// breakpoint across JS and CSS and keeps margin/width as a single source of truth.
	// The switch and the two widths come from LayoutBreakpoints so this and DesktopNavigationBar's own
	// framer-motion widths cannot drift apart. The sidebar mounts on the same predicate rather than a CSS class,
	// because the switch needs width AND height (a phone in landscape is wide but far too short for it).
	const { width, height } = useWindowDimensions();
	const isDesktopViewport = isDesktopShell(width, height);

	let contentMarginLeft = "0px";
	let contentWidth = "100%";

	if (isDesktopViewport) {
		const sidebarWidth = sideBarCollapsed ? SIDEBAR_WIDTH_COLLAPSED : SIDEBAR_WIDTH_EXPANDED;
		contentMarginLeft = `${sidebarWidth}px`;
		contentWidth = `calc(100% - ${sidebarWidth}px)`;
	}

	return (
		<>
			{/* First focusable element on every page, off-screen until it takes focus: a keyboard or screen-reader
			    user would otherwise tab through the whole sidebar and header on every navigation before reaching
			    the page. Serves both layouts — the desktop rail and the mobile drawer share this one <main>. */}
			<a href="#main-content" className="layout-skip-link">
				{t("components.layout.skipToContent")}
			</a>
			<div className="flex flex-row h-dvh w-full overflow-hidden">
				{isDesktopViewport ? (
					<DesktopNavigationBar sideBarCollapsed={sideBarCollapsed} setSideBarCollapsed={setSideBarCollapsed} />
				) : null}
				<m.div
					className="w-full flex flex-col h-dvh overflow-hidden"
					initial={false}
					layout={true}
					style={{
						marginLeft: contentMarginLeft,
						width: contentWidth,
					}}
					transition={{
						duration: 0.2,
						ease: "easeOut",
					}}
				>
					<div className="flex-shrink-0">
						<HeaderBar headerStatus={headerStatus} headerActions={headerActions} drawerActions={drawerActions} />
						{banners}
					</div>

					{/* The routed content is the `main` landmark and the skip link's target. `tabIndex={-1}` makes it
					    programmatically focusable so the jump moves the caret, not just the scroll position; it also
					    keeps this the element that scrolls, so page scrolling is unchanged. */}
					<main id="main-content" tabIndex={-1} className="flex-1 min-h-0 overflow-y-auto md:px-8 px-2 pt-2">
						<RoutedPage />
					</main>
				</m.div>
			</div>

			{overlay}

			{import.meta.env.DEV && DevelopmentUi && (
				<Suspense fallback={null}>
					<DevelopmentUi />
				</Suspense>
			)}
		</>
	);
}

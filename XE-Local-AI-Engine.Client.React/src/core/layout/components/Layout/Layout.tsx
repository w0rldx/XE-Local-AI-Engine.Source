import "./Layout.css";

import { Outlet } from "@tanstack/react-router";
import { m } from "framer-motion";
import { lazy, Suspense } from "react";

import { nodeCapabilities } from "@/capabilities/NodeCapabilities";
import { DesktopNavigationBar } from "@/core/layout/components/DesktopNavigationBar/DesktopNavigationBar";
import { HeaderBar } from "@/core/layout/components/HeaderBar/HeaderBar";
import {
	DESKTOP_NAV_BREAKPOINT,
	SIDEBAR_WIDTH_COLLAPSED,
	SIDEBAR_WIDTH_EXPANDED,
} from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { useDesktopNavigationBarStore } from "@/core/layout/stores/DesktopNavigationBarStore";
import { ChatConnectionStatusChip } from "@/features/chat/components/ChatConnectionStatusChip";
import { CpuFallbackBanner } from "@/features/model-fit/components/CpuFallbackBanner";
import { LlamaCppUpdateBanner } from "@/features/node-settings/components/LlamaCppUpdateBanner";
import { RuntimeAcquisitionBanner } from "@/features/node-settings/components/RuntimeAcquisitionBanner";

const DevelopmentUi = import.meta.env.DEV
	? lazy(() => import("@/core/dev-tools/components/DevelopmentUi/DevelopmentUi").then((m) => ({ default: m.DevelopmentUi })))
	: null;

export function Layout() {
	const sideBarCollapsed = useDesktopNavigationBarStore((state) => state.sidebarState);
	const setSideBarCollapsed = useDesktopNavigationBarStore((state) => state.actions.setSidebarState);
	// The desktop breakpoint is resolved in JS (not a CSS media query) because the
	// resulting marginLeft/width are fed into framer-motion's `animate` prop to drive
	// the 0.2s collapse/expand transition. Tradeoff: on first paint before hydration
	// `width` reflects the real window, so there is no flash in practice, but SSR/prerender
	// would briefly render the mobile (100%) layout. Keeping it here avoids duplicating the
	// breakpoint across JS and CSS and keeps margin/width as a single source of truth.
	// The cutoff and the two widths come from LayoutBreakpoints so this and DesktopNavigationBar's own
	// framer-motion widths cannot drift apart; the `md:` UnoCSS class below is the same 768px.
	const { width } = useWindowDimensions();
	const isDesktopViewport = width >= DESKTOP_NAV_BREAKPOINT;

	let contentMarginLeft = "0px";
	let contentWidth = "100%";

	if (isDesktopViewport) {
		const sidebarWidth = sideBarCollapsed ? SIDEBAR_WIDTH_COLLAPSED : SIDEBAR_WIDTH_EXPANDED;
		contentMarginLeft = `${sidebarWidth}px`;
		contentWidth = `calc(100% - ${sidebarWidth}px)`;
	}

	return (
		<>
			<div className="flex flex-row h-dvh w-full overflow-hidden">
				<div className="hidden md:block">
					<DesktopNavigationBar sideBarCollapsed={sideBarCollapsed} setSideBarCollapsed={setSideBarCollapsed} />
				</div>
				<m.div
					className="w-full flex flex-col h-dvh overflow-hidden"
					initial={false}
					animate={{
						marginLeft: contentMarginLeft,
						width: contentWidth,
					}}
					transition={{
						duration: 0.2,
						ease: "easeOut",
					}}
				>
					<div className="flex-shrink-0">
						<HeaderBar />
						{nodeCapabilities.modelFit ? <CpuFallbackBanner /> : null}
						<LlamaCppUpdateBanner />
						{/* First-run runtime acquisition. Mounted here, not on a page, because the download starts at host boot
						    and the user can be anywhere in the app while it runs. */}
						<RuntimeAcquisitionBanner />
					</div>

					<div className="flex-1 min-h-0 overflow-y-auto md:px-8 px-2 pt-2">
						<Outlet />
					</div>
				</m.div>
			</div>

			<ChatConnectionStatusChip />

			{import.meta.env.DEV && DevelopmentUi && (
				<Suspense fallback={null}>
					<DevelopmentUi />
				</Suspense>
			)}
		</>
	);
}

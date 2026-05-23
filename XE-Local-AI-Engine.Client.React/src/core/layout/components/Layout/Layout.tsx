import "./Layout.css";

import { Outlet } from "@tanstack/react-router";
import { m } from "framer-motion";
import { lazy, Suspense } from "react";

import { DesktopNavigationBar } from "@/core/layout/components/DesktopNavigationBar/DesktopNavigationBar";
import { HeaderBar } from "@/core/layout/components/HeaderBar/HeaderBar";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { useDesktopNavigationBarStore } from "@/core/layout/stores/DesktopNavigationBarStore";

const DevelopmentUi = import.meta.env.DEV
	? lazy(() => import("@/core/dev-tools/components/DevelopmentUi/DevelopmentUi").then((m) => ({ default: m.DevelopmentUi })))
	: null;

export function Layout() {
	const sideBarCollapsed = useDesktopNavigationBarStore((state) => state.sidebarState);
	const setSideBarCollapsed = useDesktopNavigationBarStore((state) => state.actions.setSidebarState);
	const { width } = useWindowDimensions();
	const isDesktopViewport = width >= 768;

	let contentMarginLeft = "0px";
	let contentWidth = "100%";

	if (isDesktopViewport) {
		if (sideBarCollapsed) {
			contentMarginLeft = "56px";
			contentWidth = "calc(100% - 56px)";
		} else {
			contentMarginLeft = "220px";
			contentWidth = "calc(100% - 220px)";
		}
	}

	return (
		<>
			<div className="flex flex-row h-dvh w-full overflow-hidden">
				<div className="hidden md:block">
					<DesktopNavigationBar sideBarCollapsed={sideBarCollapsed} setSideBarCollapsed={setSideBarCollapsed} />
				</div>
				<m.div
					className={`w-full flex flex-col h-dvh overflow-hidden ${width >= 768 && sideBarCollapsed && "main-content-sidebar-collapsed"} ${width >= 768 && !sideBarCollapsed && "main-content-sidebar-expanded"}`}
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
					</div>

					<div className="flex-1 min-h-0 overflow-y-auto md:px-8 px-2 pt-2">
						<Outlet />
					</div>
				</m.div>
			</div>

			{import.meta.env.DEV && DevelopmentUi && (
				<Suspense fallback={null}>
					<DevelopmentUi />
				</Suspense>
			)}
		</>
	);
}

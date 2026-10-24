import "./HeaderBar.css";

import { ActionIcon, Button } from "@mantine/core";
import { IconLogout, IconMenu2 as MenuIcon } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { useNodeLogout } from "@/core/auth/hooks/useNodeLogout";
import { MobileNavigationBar } from "@/core/layout/components/MobileNavigationBar/MobileNavigationBar";
import { isDesktopShell } from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import { LanguageMenu } from "@/core/locales/components/LanguageMenu/LanguageMenu";
import { ThemeModeToggle } from "@/core/theme/components/ThemeModeToggle/ThemeModeToggle";
import { AboutDialogButton } from "@/features/about/components/AboutDialogButton/AboutDialogButton";
import { ReportProblemButton } from "@/features/diagnostics/components/ReportProblemButton";
import { RuntimeResourcesWidget } from "@/features/runtime-resources/components/RuntimeResourcesWidget";

export function HeaderBar() {
	const { t } = useTranslation();
	const [drawerOpen, setDrawerOpen] = useState(false);
	const navigate = useNavigate();
	const { logout: handleLogout, logoutPending } = useNodeLogout();
	// The same switch Layout mounts the sidebar on: the desktop header pairs with the sidebar, the drawer button with
	// everything else.
	const { width, height } = useWindowDimensions();
	const isDesktop = isDesktopShell(width, height);

	return (
		<>
			<div className="header-bar w-full flex flex-row h-15 md:px-8 px-2">
				{isDesktop ? null : (
					<div className="flex flex-row items-center pl-2">
						<ActionIcon
							className="flex flex-row items-center"
							aria-label={t("components.headerBar.openDrawer")}
							onClick={() => setDrawerOpen(true)}
							variant="subtle"
							style={{ display: "flex" }}
						>
							<MenuIcon />
						</ActionIcon>
					</div>
				)}
				{/* min-w-0 + overflow-hidden: when the header crowds, the gauge gives way, never the cluster with Logout. */}
				<div className="flex-grow min-w-0 overflow-hidden flex flex-row items-center justify-start">
					{/* Desktop shell only this round, like the right cluster; the widget gates itself on capability and session. */}
					{isDesktop ? <RuntimeResourcesWidget /> : null}
				</div>
				{isDesktop ? (
					<div className="flex flex-shrink-0 flex-row items-center gap-2">
						<ThemeModeToggle />
						<ReportProblemButton
							onReported={() => {
								navigate({ to: "/diagnostics" }).catch(() => undefined);
							}}
						/>
						<AboutDialogButton />
						<LanguageMenu />
						<Button variant="subtle" leftSection={<IconLogout size={16} />} loading={logoutPending} onClick={handleLogout}>
							{t("components.headerBar.logout")}
						</Button>
					</div>
				) : null}
			</div>

			<MobileNavigationBar drawerOpen={drawerOpen} setDrawerOpen={setDrawerOpen} />
		</>
	);
}

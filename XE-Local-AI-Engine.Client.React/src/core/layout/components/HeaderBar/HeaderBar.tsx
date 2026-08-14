import "./HeaderBar.css";

import { ActionIcon, Button } from "@mantine/core";
import { IconLogout, IconMenu2 as MenuIcon } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { useNodeLogout } from "@/core/auth/hooks/useNodeLogout";
import { MobileNavigationBar } from "@/core/layout/components/MobileNavigationBar/MobileNavigationBar";
import { LanguageMenu } from "@/core/locales/components/LanguageMenu/LanguageMenu";
import { ThemeModeToggle } from "@/core/theme/components/ThemeModeToggle/ThemeModeToggle";
import { AboutDialogButton } from "@/features/about/components/AboutDialogButton/AboutDialogButton";
import { ReportProblemButton } from "@/features/diagnostics/components/ReportProblemButton";
import { ThemeConfiguratorDialogButton } from "@/modules/theme-configurator/Index";

export function HeaderBar() {
	const { t } = useTranslation();
	const [drawerOpen, setDrawerOpen] = useState(false);
	const navigate = useNavigate();
	const { logout: handleLogout, logoutPending } = useNodeLogout();

	return (
		<>
			<div className="header-bar w-full flex flex-row h-15 md:px-8 px-2">
				<div className="flex flex-row items-center pl-2 md:hidden">
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
				<div className="flex-grow" />
				<div className="hidden md:flex flex-row items-center gap-2">
					<ThemeModeToggle />
					<ThemeConfiguratorDialogButton />
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
			</div>

			<MobileNavigationBar drawerOpen={drawerOpen} setDrawerOpen={setDrawerOpen} />
		</>
	);
}

import "./HeaderBar.css";

import { ActionIcon, Button } from "@mantine/core";
import { IconLogout, IconMenu2 as MenuIcon } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { useState } from "react";
import { useTranslation } from "react-i18next";

import { logoutNodeAuth } from "@/core/auth/api/NodeAuthApi";
import { useNodeAuthStore } from "@/core/auth/stores/NodeAuthStore";
import { MobileNavigationBar } from "@/core/layout/components/MobileNavigationBar/MobileNavigationBar";
import { LanguageMenu } from "@/core/locales/components/LanguageMenu/LanguageMenu";
import { ThemeModeToggle } from "@/core/theme/components/ThemeModeToggle/ThemeModeToggle";
import { useAppTheme as useTheme } from "@/core/theme/hooks/useAppTheme";
import { AboutDialogButton } from "@/features/about/components/AboutDialogButton/AboutDialogButton";
import { ReportProblemButton } from "@/features/diagnostics/components/ReportProblemButton";
import { ThemeConfiguratorDialogButton } from "@/modules/theme-configurator/Index";

export function HeaderBar() {
	const { t } = useTranslation();
	const [drawerOpen, setDrawerOpen] = useState(false);
	const [logoutPending, setLogoutPending] = useState(false);
	const navigate = useNavigate();
	const clearAuth = useNodeAuthStore((state) => state.actions.clear);
	const theme = useTheme();

	const handleLogout = async (): Promise<void> => {
		setLogoutPending(true);
		try {
			await logoutNodeAuth();
		} finally {
			clearAuth();
			setLogoutPending(false);
			await navigate({ to: "/login" });
		}
	};

	return (
		<>
			<div className="w-full flex flex-row h-15 md:px-8 px-2" style={{ borderBottom: `1px solid ${theme.palette.divider}` }}>
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

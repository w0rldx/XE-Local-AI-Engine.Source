import "./HeaderBar.css";

import { ActionIcon } from "@mantine/core";
import { IconMenu2 as MenuIcon } from "@tabler/icons-react";
import { useState } from "react";

import { MobileNavigationBar } from "@/core/layout/components/MobileNavigationBar/MobileNavigationBar";
import { LanguageMenu } from "@/core/locales/components/LanguageMenu/LanguageMenu";
import { ThemeModeToggle } from "@/core/theme/components/ThemeModeToggle/ThemeModeToggle";
import { useAppTheme as useTheme } from "@/core/theme/hooks/useAppTheme";
import { ThemeConfiguratorDialogButton } from "@/modules/theme-configurator/Index";

export function HeaderBar() {
	const [drawerOpen, setDrawerOpen] = useState(false);
	const theme = useTheme();

	return (
		<>
			<div className="w-full flex flex-row h-15 md:px-8 px-2" style={{ borderBottom: `1px solid ${theme.palette.divider}` }}>
				<div className="flex flex-row items-center pl-2 md:hidden">
					<ActionIcon
						className="flex flex-row items-center"
						aria-label="open drawer"
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
					<LanguageMenu />
				</div>
			</div>

			<MobileNavigationBar drawerOpen={drawerOpen} setDrawerOpen={setDrawerOpen} />
		</>
	);
}

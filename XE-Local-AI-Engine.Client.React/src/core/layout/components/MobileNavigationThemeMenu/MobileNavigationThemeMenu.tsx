import { Divider, List, Text } from "@mantine/core";
import { IconCheck, IconMoon, IconSun } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { MobileNavigationDrawerPanel } from "@/core/layout/components/MobileNavigationDrawerPanel/MobileNavigationDrawerPanel";
import { SidebarMenu } from "@/core/layout/components/Sidebar/SidebarMenu";
import { SidebarMenuItem } from "@/core/layout/components/Sidebar/SidebarMenuItem";
import { useMobileNavigationDrawer } from "@/core/layout/hooks/useMobileNavigationDrawer";
import { useTheme } from "@/core/theme/hooks/useTheme";
import type { IMobileNavigationThemeMenuProperties } from "@/core/layout/components/MobileNavigationThemeMenu/MobileNavigationThemeMenu.types";

export function MobileNavigationThemeMenu({ menuItemStyle, setDrawerOpen, width }: IMobileNavigationThemeMenuProperties) {
	const { t } = useTranslation();
	const { isDrawerOpen, setIsDrawerOpen, drawerReference, menuReference, openDrawer, closeDrawer } =
		useMobileNavigationDrawer(setDrawerOpen);
	const { mode, toggleColorMode } = useTheme();

	const handleThemeChange = (selectedMode: "light" | "dark") => {
		if (mode !== selectedMode) {
			toggleColorMode();
		}
		closeDrawer();
	};

	const themeMenuItem = () => (
		<div ref={menuReference} className="h-17 flex items-center justify-center">
			<SidebarMenuItem icon={mode === "light" ? <IconSun /> : <IconMoon />} onClick={openDrawer} isMobile={true}>
				<Text size="sm" fw={500} lh="1.5">
					{t("theme.title")}
				</Text>
			</SidebarMenuItem>
		</div>
	);

	const themeOptions = [
		{ value: "light", label: t("theme.light"), icon: <IconSun /> },
		{ value: "dark", label: t("theme.dark"), icon: <IconMoon /> },
	];

	const drawerContent = (
		<MobileNavigationDrawerPanel
			isOpen={isDrawerOpen}
			width={width}
			title={t("theme.title")}
			onClose={() => setIsDrawerOpen(false)}
			drawerReference={drawerReference}
		>
			<List className="gap-2 flex flex-col">
				{themeOptions.map((option, index) => (
					<div key={option.value}>
						<div className="h-17 flex items-center">
							<SidebarMenuItem
								onClick={() => {
									handleThemeChange(option.value as "light" | "dark");
								}}
								icon={option.icon}
								active={mode === option.value}
								suffix={mode === option.value ? <IconCheck /> : undefined}
								isMobile={true}
							>
								<Text size="sm" fw={500} lh="1.5">
									{option.label}
								</Text>
							</SidebarMenuItem>
						</div>
						{index < themeOptions.length - 1 && <Divider />}
					</div>
				))}
			</List>
		</MobileNavigationDrawerPanel>
	);

	return (
		<SidebarMenu menuItemStyles={menuItemStyle}>
			{themeMenuItem()}
			{drawerContent}
		</SidebarMenu>
	);
}

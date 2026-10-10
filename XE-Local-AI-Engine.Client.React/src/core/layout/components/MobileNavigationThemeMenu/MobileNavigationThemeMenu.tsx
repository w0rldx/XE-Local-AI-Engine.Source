import {
	Divider,
	Flex,
	type MantineColorScheme,
	Stack,
	Text,
	useComputedColorScheme,
	useMantineColorScheme,
} from "@mantine/core";
import { IconCheck, IconDeviceDesktop, IconMoon, IconSun } from "@tabler/icons-react";
import type { ReactNode } from "react";
import { useTranslation } from "react-i18next";

import { MobileNavigationDrawerPanel } from "@/core/layout/components/MobileNavigationDrawerPanel/MobileNavigationDrawerPanel";
import { SidebarMenu } from "@/core/layout/components/Sidebar/SidebarMenu";
import { SidebarMenuItem } from "@/core/layout/components/Sidebar/SidebarMenuItem";
import { useMobileNavigationDrawer } from "@/core/layout/hooks/useMobileNavigationDrawer";
import type { IMobileNavigationThemeMenuProperties } from "@/core/layout/components/MobileNavigationThemeMenu/MobileNavigationThemeMenu.types";

export function MobileNavigationThemeMenu({ menuItemStyle, setDrawerOpen, width }: IMobileNavigationThemeMenuProperties) {
	const { t } = useTranslation();
	const { isDrawerOpen, setIsDrawerOpen, drawerReference, menuReference, openDrawer, closeDrawer } =
		useMobileNavigationDrawer(setDrawerOpen);
	const { colorScheme, setColorScheme } = useMantineColorScheme();
	const computedColorScheme = useComputedColorScheme("light");

	const handleThemeChange = (selectedScheme: MantineColorScheme) => {
		setColorScheme(selectedScheme);
		closeDrawer();
	};

	const themeMenuItem = () => (
		<Flex ref={menuReference} h="4.25rem" align="center" justify="center">
			<SidebarMenuItem icon={computedColorScheme === "light" ? <IconSun /> : <IconMoon />} onClick={openDrawer} isMobile={true}>
				<Text size="sm" fw={500} lh="1.5">
					{t("theme.title")}
				</Text>
			</SidebarMenuItem>
		</Flex>
	);

	const themeOptions: { value: MantineColorScheme; label: string; icon: ReactNode }[] = [
		{ value: "light", label: t("theme.light"), icon: <IconSun /> },
		{ value: "dark", label: t("theme.dark"), icon: <IconMoon /> },
		{ value: "auto", label: t("theme.system"), icon: <IconDeviceDesktop /> },
	];

	const drawerContent = (
		<MobileNavigationDrawerPanel
			isOpen={isDrawerOpen}
			width={width}
			title={t("theme.title")}
			onClose={() => setIsDrawerOpen(false)}
			drawerReference={drawerReference}
		>
			<Stack gap="0.5rem">
				{themeOptions.map((option, index) => (
					<div key={option.value}>
						<Flex h="4.25rem" align="center">
							<SidebarMenuItem
								onClick={() => {
									handleThemeChange(option.value);
								}}
								icon={option.icon}
								active={colorScheme === option.value}
								suffix={colorScheme === option.value ? <IconCheck /> : undefined}
								isMobile={true}
							>
								<Text size="sm" fw={500} lh="1.5">
									{option.label}
								</Text>
							</SidebarMenuItem>
						</Flex>
						{index < themeOptions.length - 1 && <Divider />}
					</div>
				))}
			</Stack>
		</MobileNavigationDrawerPanel>
	);

	return (
		<SidebarMenu menuItemStyles={menuItemStyle}>
			{themeMenuItem()}
			{drawerContent}
		</SidebarMenu>
	);
}

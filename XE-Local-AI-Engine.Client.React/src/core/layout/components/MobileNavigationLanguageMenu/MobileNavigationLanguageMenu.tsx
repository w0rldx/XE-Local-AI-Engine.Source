import { Divider, List, Text } from "@mantine/core";
import { IconCheck, IconLanguage } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import { MobileNavigationDrawerPanel } from "@/core/layout/components/MobileNavigationDrawerPanel/MobileNavigationDrawerPanel";
import { SidebarMenu } from "@/core/layout/components/Sidebar/SidebarMenu";
import { SidebarMenuItem } from "@/core/layout/components/Sidebar/SidebarMenuItem";
import { useMobileNavigationDrawer } from "@/core/layout/hooks/useMobileNavigationDrawer";
import { useUserLanguageStore } from "@/core/locales/stores/UserLanguageStore";
import { languageData } from "@/data/language/LanguageMenuData";
import type { IMobileNavigationLanguageMenuProperties } from "@/core/layout/components/MobileNavigationLanguageMenu/MobileNavigationLanguageMenu.types";

export function MobileNavigationLanguageMenu({
	theme,
	menuItemStyle,
	setDrawerOpen,
	width,
}: IMobileNavigationLanguageMenuProperties) {
	const { t, i18n } = useTranslation();
	const { isDrawerOpen, setIsDrawerOpen, drawerReference, menuReference, openDrawer, closeDrawer } =
		useMobileNavigationDrawer(setDrawerOpen);
	const { selectedApplicationLanguage, changeLanguage } = useUserLanguageStore();

	const handleLanguageChange = async (language: string) => {
		await i18n.changeLanguage(language);
		changeLanguage(language);
		closeDrawer();
	};

	const languageMenuItem = () => (
		<div ref={menuReference} className="h-17 flex items-center justify-center">
			<SidebarMenuItem icon={<IconLanguage />} onClick={openDrawer} isMobile={true}>
				<Text size="sm" fw={500} lh="1.5">
					{t("components.mobileNavigation.languageTitle")}
				</Text>
			</SidebarMenuItem>
		</div>
	);

	const drawerContent = (
		<MobileNavigationDrawerPanel
			isOpen={isDrawerOpen}
			theme={theme}
			width={width}
			title={t("components.mobileNavigation.languageTitle")}
			onClose={() => setIsDrawerOpen(false)}
			drawerReference={drawerReference}
		>
			<List className="gap-2 flex flex-col">
				{languageData.map((language) => (
					<div key={language.value}>
						<div className="h-17 flex items-center">
							<SidebarMenuItem
								onClick={() => {
									handleLanguageChange(language.value);
								}}
								active={selectedApplicationLanguage === language.value}
								suffix={selectedApplicationLanguage === language.value ? <IconCheck /> : undefined}
								isMobile={true}
							>
								<Text size="sm" fw={500} lh="1.5">
									{language.text}
								</Text>
							</SidebarMenuItem>
						</div>
						<Divider />
					</div>
				))}
			</List>
		</MobileNavigationDrawerPanel>
	);

	return (
		<SidebarMenu menuItemStyles={menuItemStyle}>
			{languageMenuItem()}
			{drawerContent}
		</SidebarMenu>
	);
}

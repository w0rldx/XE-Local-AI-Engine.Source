import "./MobileNavigationBar.css";

import { ActionIcon, Divider, Drawer } from "@mantine/core";
import { IconX } from "@tabler/icons-react";
import { useNavigate, useRouterState } from "@tanstack/react-router";
import { useTranslation } from "react-i18next";

import { LogoCombined } from "@/components/Logo/LogoCombined";
import { MobileNavigationLanguageMenu } from "@/core/layout/components/MobileNavigationLanguageMenu/MobileNavigationLanguageMenu";
import type { IMobileNavigationBarProperties } from "@/core/layout/components/MobileNavigationBar/MobileNavigationBar.types";
import { MobileNavigationMenu } from "@/core/layout/components/MobileNavigationMenu/MobileNavigationMenu";
import { MobileNavigationThemeMenu } from "@/core/layout/components/MobileNavigationThemeMenu/MobileNavigationThemeMenu";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import type { MenuItemStyles } from "@/core/layout/models/Sidebar";
import { useAppTheme as useTheme } from "@/core/theme/hooks/useAppTheme";
import { matchesNavRoute, navigationLinks } from "@/data/navigation/NavigationMenuData";

export function MobileNavigationBar({ drawerOpen, setDrawerOpen }: IMobileNavigationBarProperties) {
	const { width } = useWindowDimensions();
	const theme = useTheme();
	const { t } = useTranslation();
	const navigate = useNavigate();
	const pathname = useRouterState({ select: (state) => state.location.pathname });

	const menuItemStyle: MenuItemStyles = {
		root: {
			fontSize: "13px",
			fontWeight: 400,
		},
		subMenuExpandIcon: {
			color: theme.palette.text.primary,
		},
		subMenuContent: () => ({
			backgroundColor: theme.palette.background.default,
		}),
		button: {
			"&:hover": {
				backgroundColor: theme.palette.background.default,
			},
		},
		label: () => ({
			fontWeight: 500,
		}),
	};

	const viewableNavigationMenus = (links: typeof navigationLinks) => {
		const viewableMenus = [];

		for (const link of links) {
			if (link.links && link.links.length > 0) {
				viewableMenus.push({
					menuId: link.id,
					menuItem: {
						icon: <link.icon size={24} />,
						label: t(link.translationKey),
						onClick: link.to ? () => navigate({ to: link.to }) : undefined,
						// A group is highlighted when the active route lives under one of its children.
						active: link.links.some((nestedLink) => matchesNavRoute(pathname, nestedLink.to)),
					},
					drawerTitle: t(link.translationKey),
					links: link.links.map((nestedLink) => ({
						label: t(nestedLink.translationKey),
						to: nestedLink.to,
						onClick: nestedLink.onClick,
						active: matchesNavRoute(pathname, nestedLink.to),
					})),
				});
			} else if (link.to || link.onClick) {
				viewableMenus.push({
					menuId: link.id,
					menuItem: {
						icon: <link.icon size={24} />,
						label: t(link.translationKey),
						active: matchesNavRoute(pathname, link.to),
						onClick: link.onClick ?? (link.to ? () => navigate({ to: link.to }) : undefined),
					},
				});
			}
		}

		return viewableMenus;
	};

	return (
		<Drawer
			position="left"
			opened={drawerOpen}
			onClose={() => setDrawerOpen(false)}
			withCloseButton={false}
			withOverlay={true}
			overlayProps={{ backgroundOpacity: 0.5, blur: 0 }}
			className="flex flex-col h-full"
			styles={{
				content: {
					width: width < 420 ? "100%" : "min(400px, 100vw)",
					backgroundColor: theme.palette.background.default,
				},
			}}
		>
			<div className="flex flex-row justify-between items-center pt-3 pb-1 pl-7 pr-2 h-15">
				<div className="h-12 pt-2">
					<LogoCombined />
				</div>
				<ActionIcon onClick={() => setDrawerOpen(false)} variant="subtle">
					<IconX size={24} />
				</ActionIcon>
			</div>
			<div className="flex flex-col gap-1 h-full pt-3">
				<Divider />

				{/* Regular Navigation Menus */}
				{viewableNavigationMenus(navigationLinks).map((menu) => (
					<MobileNavigationMenu
						key={menu.menuId}
						menuItemStyle={menuItemStyle}
						theme={theme}
						setDrawerOpen={setDrawerOpen}
						menuItem={menu.menuItem}
						drawerTitle={menu.drawerTitle}
						links={menu.links}
						width={width}
					/>
				))}

				<div className="flex-grow" />

				<MobileNavigationThemeMenu theme={theme} menuItemStyle={menuItemStyle} setDrawerOpen={setDrawerOpen} width={width} />

				<MobileNavigationLanguageMenu theme={theme} menuItemStyle={menuItemStyle} setDrawerOpen={setDrawerOpen} width={width} />
			</div>
		</Drawer>
	);
}

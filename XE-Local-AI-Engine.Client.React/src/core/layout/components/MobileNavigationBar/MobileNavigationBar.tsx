import "./MobileNavigationBar.css";

import { ActionIcon, Divider, Drawer, Text } from "@mantine/core";
import { useDisclosure } from "@mantine/hooks";
import { IconBug, IconInfoCircle, IconLogout, IconX } from "@tabler/icons-react";
import { useNavigate, useRouterState } from "@tanstack/react-router";
import { lazy, Suspense } from "react";
import { useTranslation } from "react-i18next";

import { LogoCombined } from "@/components/Logo/LogoCombined";
import { useNodeLogout } from "@/core/auth/hooks/useNodeLogout";
import type { IMobileNavigationBarProperties } from "@/core/layout/components/MobileNavigationBar/MobileNavigationBar.types";
import { MobileNavigationLanguageMenu } from "@/core/layout/components/MobileNavigationLanguageMenu/MobileNavigationLanguageMenu";
import { MobileNavigationMenu } from "@/core/layout/components/MobileNavigationMenu/MobileNavigationMenu";
import { MobileNavigationThemeMenu } from "@/core/layout/components/MobileNavigationThemeMenu/MobileNavigationThemeMenu";
import { SidebarMenu } from "@/core/layout/components/Sidebar/SidebarMenu";
import { SidebarMenuItem } from "@/core/layout/components/Sidebar/SidebarMenuItem";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
import type { MenuItemStyles } from "@/core/layout/models/Sidebar";
import { useAppTheme as useTheme } from "@/core/theme/hooks/useAppTheme";
import { matchesNavRoute, navigationLinks } from "@/data/navigation/NavigationMenuData";
import { useReportProblem } from "@/features/diagnostics/hooks/useReportProblem";

// Same lazy pattern as AboutDialogButton: the dialog bundle only loads on first open.
const AboutDialog = lazy(async () => {
	const module = await import("@/features/about/components/AboutDialog/AboutDialog");
	return { default: module.AboutDialog };
});

export function MobileNavigationBar({ drawerOpen, setDrawerOpen }: IMobileNavigationBarProperties) {
	const { width } = useWindowDimensions();
	const theme = useTheme();
	const { t } = useTranslation();
	const navigate = useNavigate();
	const pathname = useRouterState({ select: (state) => state.location.pathname });
	const { logout, logoutPending } = useNodeLogout();
	const [aboutOpened, { open: openAbout, close: closeAbout }] = useDisclosure(false);
	const { report, pending: reportPending } = useReportProblem(() => {
		navigate({ to: "/diagnostics" }).catch(() => undefined);
	});

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
		<>
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

					{/* Report problem + About + Logout live in the desktop HeaderBar; on mobile the drawer is the
				    only chrome, so it must offer them too. ThemeConfigurator stays desktop-only (palette editor). */}
					<SidebarMenu menuItemStyles={menuItemStyle}>
						<div className="h-17 flex items-center justify-center">
							<SidebarMenuItem
								icon={<IconBug />}
								disabled={reportPending}
								onClick={() => {
									setDrawerOpen(false);
									report().catch(() => undefined);
								}}
								isMobile={true}
							>
								<Text size="sm" fw={500} lh="1.5">
									{t("diagnostics.reportProblem")}
								</Text>
							</SidebarMenuItem>
						</div>
					</SidebarMenu>

					<SidebarMenu menuItemStyles={menuItemStyle}>
						<div className="h-17 flex items-center justify-center">
							<SidebarMenuItem
								icon={<IconInfoCircle />}
								onClick={() => {
									setDrawerOpen(false);
									openAbout();
								}}
								isMobile={true}
							>
								<Text size="sm" fw={500} lh="1.5">
									{t("pages.about.title", "About")}
								</Text>
							</SidebarMenuItem>
						</div>
					</SidebarMenu>

					<SidebarMenu menuItemStyles={menuItemStyle}>
						<div className="h-17 flex items-center justify-center">
							<SidebarMenuItem
								icon={<IconLogout />}
								disabled={logoutPending}
								onClick={() => {
									setDrawerOpen(false);
									logout().catch(() => undefined);
								}}
								isMobile={true}
							>
								<Text size="sm" fw={500} lh="1.5">
									{t("components.headerBar.logout")}
								</Text>
							</SidebarMenuItem>
						</div>
					</SidebarMenu>
				</div>
			</Drawer>

			{/* Rendered outside the Drawer so the dialog survives the drawer closing when About is opened. */}
			{aboutOpened ? (
				<Suspense fallback={null}>
					<AboutDialog opened={aboutOpened} onClose={closeAbout} />
				</Suspense>
			) : null}
		</>
	);
}

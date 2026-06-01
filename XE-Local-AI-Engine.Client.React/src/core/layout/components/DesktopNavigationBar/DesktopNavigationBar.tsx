import { Menu, ScrollArea, Text, Tooltip, UnstyledButton } from "@mantine/core";
import { IconLayoutSidebarLeftCollapse, IconLayoutSidebarLeftExpand } from "@tabler/icons-react";
import { useNavigate } from "@tanstack/react-router";
import { m } from "framer-motion";
import { useMemo } from "react";
import { useTranslation } from "react-i18next";

import { LogoMark } from "@/components/Logo/LogoMark";
import { LogoText } from "@/components/Logo/LogoText";
import type {
	IDesktopNavigationBarProperties,
	IViewableNavigationLink,
} from "@/core/layout/components/DesktopNavigationBar/DesktopNavigationBar.types";
import type { INavigationLink } from "@/data/navigation/NavigationMenuData";
import { navigationLinks } from "@/data/navigation/NavigationMenuData";

import classes from "./DesktopNavigationBar.module.css";
import {
	labelVariants,
	logoMarkVariants,
	MOTION_SPEC,
	navVariants,
	nestedLinksVariants,
} from "@/core/layout/components/DesktopNavigationBar/DesktopNavigationBar.animations";

export function DesktopNavigationBar({ sideBarCollapsed, setSideBarCollapsed }: IDesktopNavigationBarProperties) {
	const { t } = useTranslation();
	const navigate = useNavigate();

	const viewableNavigationLinks = useMemo(() => {
		const mapViewableNavigationLinks = (links: readonly INavigationLink[]): IViewableNavigationLink[] => {
			const viewableLinks: IViewableNavigationLink[] = [];

			for (const link of links) {
				if (link.links && link.links.length > 0) {
					viewableLinks.push({
						id: link.id,
						icon: link.icon,
						label: t(link.translationKey),
						to: link.to,
						onClick: link.onClick,
						nestedLinks: link.links.map((nestedLink) => ({
							label: t(nestedLink.translationKey),
							to: nestedLink.to,
						})),
					});

					continue;
				}

				if (link.to || link.onClick) {
					viewableLinks.push({
						id: link.id,
						icon: link.icon,
						label: t(link.translationKey),
						to: link.to,
						onClick: link.onClick,
					});
				}
			}

			return viewableLinks;
		};

		return mapViewableNavigationLinks(navigationLinks);
	}, [t]);

	const handleNavigate = (to: string, onClick?: () => void) => {
		if (onClick) {
			onClick();
		} else {
			navigate({ to });
		}
	};

	const toggleSidebar = () => {
		setSideBarCollapsed(!sideBarCollapsed);
	};

	const renderNavigationItem = (item: IViewableNavigationLink) => {
		const handleControlClick = () => {
			if (item.onClick) {
				item.onClick();
				return;
			}

			if (item.to) {
				handleNavigate(item.to);
			}
		};

		const hasNestedLinks = (item.nestedLinks?.length ?? 0) > 0;

		const itemControl = (
			<UnstyledButton onClick={handleControlClick} className={classes["control"]}>
				<m.div
					className={classes["control-content"]}
					animate={{ gap: sideBarCollapsed ? 0 : 12 }}
					initial={false}
					transition={MOTION_SPEC}
				>
					<span className={classes["icon-slot"]}>
						<item.icon size={20} />
					</span>
					<m.div variants={labelVariants} initial={false} className={classes["control-label-motion"]}>
						<Text size="sm" fw={500}>
							{item.label}
						</Text>
					</m.div>
				</m.div>
			</UnstyledButton>
		);

		// Wrap the button in Tooltip or Menu depending on collapsed state
		let buttonWrapper;
		if (sideBarCollapsed && hasNestedLinks) {
			buttonWrapper = (
				<Menu withinPortal={true} position="right-start" shadow="md">
					<Menu.Target>
						<div className={classes["collapsed-menu-target"]}>{itemControl}</div>
					</Menu.Target>
					<Menu.Dropdown>
						{item.nestedLinks!.map((nestedLink) => (
							<Menu.Item key={nestedLink.to} onClick={() => handleNavigate(nestedLink.to)}>
								{nestedLink.label}
							</Menu.Item>
						))}
					</Menu.Dropdown>
				</Menu>
			);
		} else if (sideBarCollapsed) {
			buttonWrapper = (
				<Tooltip label={item.label} position="right">
					<span>{itemControl}</span>
				</Tooltip>
			);
		} else {
			buttonWrapper = itemControl;
		}

		// Nested links are always rendered as a motion.div so they animate in/out
		// rather than appearing/disappearing instantly when the sidebar state changes.
		return (
			<div key={item.id}>
				{buttonWrapper}
				{hasNestedLinks && (
					<m.div
						variants={nestedLinksVariants}
						initial={false}
						transition={{ duration: 0.15, ease: "easeOut" }}
						className={classes["nested-links"]}
					>
						{item.nestedLinks!.map((nestedLink) => (
							<UnstyledButton key={nestedLink.to} onClick={() => handleNavigate(nestedLink.to)} className={classes["link"]}>
								{nestedLink.label}
							</UnstyledButton>
						))}
					</m.div>
				)}
			</div>
		);
	};

	return (
		<m.nav
			className={classes["navbar"]}
			variants={navVariants}
			initial={false}
			animate={sideBarCollapsed ? "collapsed" : "expanded"}
			transition={MOTION_SPEC}
			data-collapsed={sideBarCollapsed ? "true" : "false"}
		>
			<div className={classes["header"]}>
				<m.div variants={logoMarkVariants} initial={false} transition={MOTION_SPEC} className={classes["logo-mark-wrapper"]}>
					<LogoMark />
				</m.div>
				<m.div variants={labelVariants} initial={false} transition={MOTION_SPEC} className={classes["logo-text-motion"]}>
					<LogoText />
				</m.div>
			</div>

			<ScrollArea className={classes["links"]}>
				<div className={classes["links-inner"]}>{viewableNavigationLinks.map((item) => renderNavigationItem(item))}</div>
			</ScrollArea>

			<div className={classes["footer"]}>
				<div className={classes["toggle-row"]}>
					<Tooltip label={t("components.sideNavigationBar.tooltip.expand")} position="right" disabled={!sideBarCollapsed}>
						<UnstyledButton
							onClick={toggleSidebar}
							className={classes["toggle-button"]}
							aria-label={sideBarCollapsed ? t("components.sideNavigationBar.ariaExpand") : t("components.sideNavigationBar.ariaCollapse")}
						>
							{sideBarCollapsed ? <IconLayoutSidebarLeftExpand size={20} /> : <IconLayoutSidebarLeftCollapse size={20} />}
						</UnstyledButton>
					</Tooltip>
				</div>
			</div>
		</m.nav>
	);
}

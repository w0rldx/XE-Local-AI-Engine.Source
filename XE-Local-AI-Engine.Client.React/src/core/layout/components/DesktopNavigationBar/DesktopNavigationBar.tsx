import { Collapse, Menu, ScrollArea, Text, Tooltip, UnstyledButton } from "@mantine/core";
import { IconChevronRight, IconLayoutSidebarLeftCollapse, IconLayoutSidebarLeftExpand } from "@tabler/icons-react";
import { Link, useRouterState } from "@tanstack/react-router";
import { m } from "framer-motion";
import { useId, useMemo } from "react";
import { useTranslation } from "react-i18next";

import { LogoMark } from "@/components/Logo/LogoMark";
import { LogoText } from "@/components/Logo/LogoText";
import type {
	IDesktopNavigationBarProperties,
	IViewableNavigationLink,
} from "@/core/layout/components/DesktopNavigationBar/DesktopNavigationBar.types";
import { useDesktopNavigationBarStore } from "@/core/layout/stores/DesktopNavigationBarStore";
import type { INavigationLink } from "@/data/navigation/NavigationMenuData";
import {
	filterNavigationLinksByUiMode,
	matchesNavRoute,
	navigationLinks,
	navLinkActiveOptions,
} from "@/data/navigation/NavigationMenuData";
import { useUiMode } from "@/core/layout/hooks/useUiMode";

import classes from "./DesktopNavigationBar.module.css";
import {
	labelVariants,
	logoMarkVariants,
	MOTION_SPEC,
	navVariants,
} from "@/core/layout/components/DesktopNavigationBar/DesktopNavigationBar.animations";

export function DesktopNavigationBar({ sideBarCollapsed, setSideBarCollapsed }: IDesktopNavigationBarProperties) {
	const { t } = useTranslation();
	const pathname = useRouterState({ select: (state) => state.location.pathname });
	// Prefix for the nested-links containers a group toggle points `aria-controls` at. React owns it, so two
	// mounted navigation bars (app + a test render) cannot collide on the same DOM id.
	const groupIdPrefix = useId();
	// The second nav gate: capability (compile-time, already applied to `navigationLinks`) then mode (live, from the
	// node settings). Changing the mode in Node Settings re-renders this memo on the next commit — no reload.
	const uiMode = useUiMode();

	// Explicit open/closed state per group id, persisted so the user's choices survive a reload. A group with
	// no explicit entry falls back to "open when it contains the active route" so the active page is always
	// revealed without the user expanding it first.
	const openGroups = useDesktopNavigationBarStore((state) => state.openGroups);
	const setGroupOpen = useDesktopNavigationBarStore((state) => state.actions.setGroupOpen);

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

		return mapViewableNavigationLinks(filterNavigationLinksByUiMode(navigationLinks, uiMode));
	}, [t, uiMode]);

	const toggleSidebar = () => {
		setSideBarCollapsed(!sideBarCollapsed);
	};

	const isGroupActive = (item: IViewableNavigationLink) =>
		item.nestedLinks?.some((nestedLink) => matchesNavRoute(pathname, nestedLink.to)) ?? false;

	const isGroupOpen = (item: IViewableNavigationLink) => openGroups[item.id] ?? isGroupActive(item);

	const renderNavigationItem = (item: IViewableNavigationLink) => {
		const hasNestedLinks = (item.nestedLinks?.length ?? 0) > 0;
		const groupActive = hasNestedLinks && isGroupActive(item);
		const itemActive = !hasNestedLinks && matchesNavRoute(pathname, item.to);
		const open = hasNestedLinks && isGroupOpen(item);
		// Only the expanded rail renders the Collapse; the collapsed rail swaps in a Menu, which owns its own
		// expanded state, so the disclosure wiring must not be announced there.
		const isDisclosure = hasNestedLinks && !sideBarCollapsed;
		const nestedLinksId = `${groupIdPrefix}${item.id}`;

		const handleControlClick = () => {
			if (hasNestedLinks) {
				// Group parent is a pure toggle: flip its explicit open state (seeded from the active-aware default).
				setGroupOpen(item.id, !isGroupOpen(item));
				return;
			}

			item.onClick?.();
		};

		// A flat item with a route renders as a real anchor so middle-click, ctrl-click and "open in new tab" work
		// and a screen reader announces a link rather than a button. An `onClick` still wins over `to` (the old
		// handler ran it INSTEAD of navigating), and a group parent is a pure disclosure toggle, so both stay
		// <button>s. `aria-current` keeps coming from matchesNavRoute — Link's own active props would be a second,
		// differently-scoped notion of "active" next to the one the whole rail already agrees on.
		const navigatesByHref = !hasNestedLinks && !item.onClick && item.to !== undefined;

		const controlClassName = `${classes["control"]}${groupActive || itemActive ? ` ${classes["control-active"]}` : ""}`;

		const controlProps = {
			className: controlClassName,
			"aria-current": itemActive ? ("page" as const) : undefined,
			"aria-expanded": isDisclosure ? open : undefined,
			"aria-controls": isDisclosure ? nestedLinksId : undefined,
			"data-tour": `nav-item-${item.id}`,
		};

		const controlBody = (
			<m.div
				className={classes["control-content"]}
				layout={true}
				style={{ gap: sideBarCollapsed ? 0 : 12 }}
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
				{/* Chevron is only meaningful for an expandable group in the expanded rail; the collapsed rail
				    swaps in a flyout menu (below) so children stay reachable without the chevron. */}
				{hasNestedLinks && !sideBarCollapsed && (
					<m.div variants={labelVariants} initial={false} className={classes["chevron-slot"]}>
						<IconChevronRight size={16} className={`${classes["chevron"]}${open ? ` ${classes["chevron-open"]}` : ""}`} />
					</m.div>
				)}
			</m.div>
		);

		const itemControl = navigatesByHref ? (
			<UnstyledButton component={Link} to={item.to} activeOptions={navLinkActiveOptions} {...controlProps}>
				{controlBody}
			</UnstyledButton>
		) : (
			<UnstyledButton onClick={handleControlClick} {...controlProps}>
				{controlBody}
			</UnstyledButton>
		);

		// Wrap the button in Tooltip or Menu depending on collapsed state
		let buttonWrapper;
		if (sideBarCollapsed && hasNestedLinks) {
			buttonWrapper = (
				<Menu withinPortal={true} position="right-start" shadow="md" trigger="click-hover" openDelay={80}>
					<Menu.Target>
						<div className={classes["collapsed-menu-target"]}>{itemControl}</div>
					</Menu.Target>
					<Menu.Dropdown>
						<Menu.Label>{item.label}</Menu.Label>
						{item.nestedLinks!.map((nestedLink) => {
							const nestedActive = matchesNavRoute(pathname, nestedLink.to);
							return (
								<Menu.Item
									key={nestedLink.to}
									component={Link}
									to={nestedLink.to}
									activeOptions={navLinkActiveOptions}
									aria-current={nestedActive ? "page" : undefined}
								>
									<Text
										size="sm"
										fw={nestedActive ? 600 : 400}
										c={nestedActive ? "var(--mantine-primary-color-light-color)" : undefined}
									>
										{nestedLink.label}
									</Text>
								</Menu.Item>
							);
						})}
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

		// In the expanded rail, nested links sit under their group parent inside a Mantine Collapse. Collapse
		// measures the content and animates height cleanly (no auto-height jump) and respects reduced motion.
		return (
			<div key={item.id}>
				{buttonWrapper}
				{isDisclosure && (
					// The id lives on the Collapse, not on the inner column: Collapse only keeps its CONTENT mounted
					// while open, so an id one level down would be a dangling aria-controls target whenever the group
					// is closed — exactly the state the attribute has to describe.
					<Collapse id={nestedLinksId} expanded={open} transitionDuration={150}>
						<div className={classes["nested-links"]}>
							{item.nestedLinks!.map((nestedLink) => {
								const nestedActive = matchesNavRoute(pathname, nestedLink.to);
								return (
									<UnstyledButton
										key={nestedLink.to}
										component={Link}
										to={nestedLink.to}
										activeOptions={navLinkActiveOptions}
										className={`${classes["link"]}${nestedActive ? ` ${classes["link-active"]}` : ""}`}
										aria-current={nestedActive ? "page" : undefined}
									>
										{nestedLink.label}
									</UnstyledButton>
								);
							})}
						</div>
					</Collapse>
				)}
			</div>
		);
	};

	return (
		<m.nav
			className={classes["navbar"]}
			aria-label={t("components.sideNavigationBar.ariaLabel")}
			variants={navVariants}
			initial={false}
			animate={sideBarCollapsed ? "collapsed" : "expanded"}
			transition={MOTION_SPEC}
			data-collapsed={sideBarCollapsed ? "true" : "false"}
			data-tour="nav-sidebar"
		>
			<div className={classes["header"]}>
				<m.div variants={logoMarkVariants} initial={false} transition={MOTION_SPEC} className={classes["logo-mark-wrapper"]}>
					<LogoMark />
				</m.div>
				<m.div variants={labelVariants} initial={false} transition={MOTION_SPEC} className={classes["logo-text-motion"]}>
					<LogoText className="h-5 w-auto" />
				</m.div>
			</div>

			{/* Every prop here is deliberate and overrides the app-wide ScrollArea defaults set in ThemeProvider:
			    a 6px bar (the 12px default painted over the chevrons and the active highlight), `scrollbars="y"`
			    because the inner column already clips X, and `offsetScrollbars={false}` because the theme's
			    "present" would pad the content away from the bar and undo the gutter the CSS just carved out.
			    `type="auto"` is the theme default, repeated so the whole scroll contract reads in one place. */}
			<ScrollArea className={classes["links"]} type="auto" scrollbarSize={6} scrollbars="y" offsetScrollbars={false}>
				<div className={classes["links-inner"]}>{viewableNavigationLinks.map((item) => renderNavigationItem(item))}</div>
			</ScrollArea>

			<div className={classes["footer"]}>
				<div className={classes["toggle-row"]}>
					<Tooltip label={t("components.sideNavigationBar.tooltip.expand")} position="right" disabled={!sideBarCollapsed}>
						<UnstyledButton
							onClick={toggleSidebar}
							className={classes["toggle-button"]}
							aria-label={
								sideBarCollapsed ? t("components.sideNavigationBar.ariaExpand") : t("components.sideNavigationBar.ariaCollapse")
							}
						>
							{sideBarCollapsed ? <IconLayoutSidebarLeftExpand size={20} /> : <IconLayoutSidebarLeftCollapse size={20} />}
						</UnstyledButton>
					</Tooltip>
				</div>
			</div>
		</m.nav>
	);
}

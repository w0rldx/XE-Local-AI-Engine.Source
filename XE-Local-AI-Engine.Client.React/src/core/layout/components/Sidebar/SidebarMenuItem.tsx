import { Link } from "@tanstack/react-router";
import type { CSSProperties } from "react";

import type { MenuItemProperties } from "@/core/layout/models/Sidebar";
import { useSidebarStore } from "@/core/layout/stores/SidebarStore";

import "./SidebarMenuItem.css";

const EMPTY_ROOT_STYLES: CSSProperties = {};

// Link stamps its own `aria-current` from a prefix match the caller cannot override; exact matching keeps it from
// marking a sibling entry (/training on /training/datasets). Same rule, and same reason, as `navLinkActiveOptions` in
// NavigationMenuData — restated here because core may not import from the navigation data module.
const EXACT_ACTIVE_OPTIONS = { exact: true } as const;

export function SidebarMenuItem({
	children,
	ref: reference,
	icon,
	active = false,
	disabled = false,
	prefix,
	suffix,
	component,
	rootStyles = EMPTY_ROOT_STYLES,
	to,
	onClick,
	className = "",
	isMobile = false,
}: MenuItemProperties) {
	const isCollapsed = useSidebarStore((state) => state.collapsed) && !isMobile;
	const shouldCenter = isCollapsed && !isMobile;

	const activateMenuItem = () => {
		if (!disabled && onClick) {
			onClick();
		}
	};

	// Link only suppresses ITS OWN navigation for a modified click ("open in new tab/window"); a caller's onClick
	// still runs, which would close the drawer in the tab the user deliberately stayed in. Same predicate the
	// router uses, applied where every mobile link click passes.
	const handleLinkClick = (event: React.MouseEvent<HTMLAnchorElement>) => {
		if (event.metaKey || event.altKey || event.ctrlKey || event.shiftKey || event.button !== 0) {
			return;
		}

		activateMenuItem();
	};

	const handleKeyDown = (event: React.KeyboardEvent) => {
		if ((event.key === "Enter" || event.key === " ") && !disabled && onClick) {
			event.preventDefault();
			onClick();
		}
	};

	if (component && typeof component !== "string") {
		return (
			<div ref={reference} className={`custom-menu-item ${active ? "active" : ""} ${disabled ? "disabled" : ""} ${className}`}>
				{component}
			</div>
		);
	}

	const itemBody = (
		<>
			{prefix && !isCollapsed && <div className="sidebar-menu-item-prefix">{prefix}</div>}

			{icon && (
				<div className="sidebar-menu-item-icon" data-centered={shouldCenter || undefined}>
					{icon}
				</div>
			)}

			{!isCollapsed && <span className="sidebar-menu-item-label">{children}</span>}

			{suffix && !isCollapsed && <div className="sidebar-menu-item-suffix">{suffix}</div>}
		</>
	);

	return (
		<div
			ref={reference}
			className={`custom-menu-item ${active ? "active" : ""} ${disabled ? "disabled" : ""} ${className}`}
			data-centered={shouldCenter || undefined}
			style={{
				...rootStyles,
			}}
		>
			{/* An item that leads somewhere is an anchor, so middle-click, ctrl-click and "open in new tab" work and a
			    screen reader announces a link. `onClick` still runs on a plain click (it closes the drawer) and the
			    router's own handler takes the navigation. A disabled item has no href to offer, so it falls back to
			    the button. */}
			{to !== undefined && !disabled ? (
				<Link
					to={to}
					activeOptions={EXACT_ACTIVE_OPTIONS}
					className="sidebar-menu-item-button"
					onClick={handleLinkClick}
					data-centered={shouldCenter || undefined}
					data-active={active || undefined}
					aria-current={active ? "page" : undefined}
				>
					{itemBody}
				</Link>
			) : (
				<button
					className="sidebar-menu-item-button"
					onClick={activateMenuItem}
					onKeyDown={handleKeyDown}
					disabled={disabled}
					aria-disabled={disabled}
					data-centered={shouldCenter || undefined}
					type="button"
					data-active={active || undefined}
				>
					{itemBody}
				</button>
			)}
		</div>
	);
}

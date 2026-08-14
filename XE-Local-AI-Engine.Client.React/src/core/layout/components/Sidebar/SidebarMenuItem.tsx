import type { CSSProperties } from "react";

import type { MenuItemProperties } from "@/core/layout/models/Sidebar";
import { useSidebarStore } from "@/core/layout/stores/SidebarStore";

import "./SidebarMenuItem.css";

const EMPTY_ROOT_STYLES: CSSProperties = {};

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

	return (
		<div
			ref={reference}
			className={`custom-menu-item ${active ? "active" : ""} ${disabled ? "disabled" : ""} ${className}`}
			data-centered={shouldCenter || undefined}
			style={{
				...rootStyles,
			}}
		>
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
				{prefix && !isCollapsed && <div className="sidebar-menu-item-prefix">{prefix}</div>}

				{icon && (
					<div className="sidebar-menu-item-icon" data-centered={shouldCenter || undefined}>
						{icon}
					</div>
				)}

				{!isCollapsed && <span className="sidebar-menu-item-label">{children}</span>}

				{suffix && !isCollapsed && <div className="sidebar-menu-item-suffix">{suffix}</div>}
			</button>
		</div>
	);
}

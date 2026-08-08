import type { CSSProperties } from "react";

import type { MenuProperties } from "@/core/layout/models/Sidebar";

const EMPTY_ROOT_STYLES: CSSProperties = {};

export function SidebarMenu({ children, ref: reference, rootStyles = EMPTY_ROOT_STYLES, className = "" }: MenuProperties) {
	return (
		<div
			ref={reference}
			className={`custom-menu ${className}`}
			role="menu"
			style={{
				display: "flex",
				flexDirection: "column",
				listStyle: "none",
				margin: 0,
				padding: 0,
				...rootStyles,
			}}
		>
			{children}
		</div>
	);
}

import type { Dispatch, ReactNode, SetStateAction } from "react";

import type { MenuItemStyles } from "@/core/layout/models/Sidebar";

interface IMobileNavigationMenuItem {
	icon: ReactNode;
	label: string;
	onClick?: () => void;
	active?: boolean;
}

export interface IMobileNavigationMenuLink {
	label: string;
	to?: string;
	onClick?: () => void;
	icon?: ReactNode;
	active?: boolean;
}

export interface IMobileNavigationMenuProperties {
	menuItemStyle: MenuItemStyles;
	setDrawerOpen: Dispatch<SetStateAction<boolean>>;
	menuItem: IMobileNavigationMenuItem;
	drawerTitle?: string;
	links?: IMobileNavigationMenuLink[];
	shouldRender?: boolean;
	width: number;
}

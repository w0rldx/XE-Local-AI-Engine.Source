import type { Dispatch, ReactNode, SetStateAction } from "react";

import type { MenuItemStyles } from "@/core/layout/models/Sidebar";

interface IMobileNavigationMenuItem {
	icon: ReactNode;
	label: string;
	/** Short tier label rendered beside the label (External Apps: Experimental). */
	badge?: string;
	to?: string;
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

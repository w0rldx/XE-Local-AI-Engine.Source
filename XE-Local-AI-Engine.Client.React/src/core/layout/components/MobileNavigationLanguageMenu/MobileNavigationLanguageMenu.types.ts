import type { MenuItemStyles } from "@/core/layout/models/Sidebar";
import type { Dispatch, SetStateAction } from "react";

export interface IMobileNavigationLanguageMenuProperties {
	menuItemStyle: MenuItemStyles;
	setDrawerOpen: Dispatch<SetStateAction<boolean>>;
	width: number;
}

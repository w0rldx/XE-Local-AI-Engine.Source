import type { MenuItemStyles } from "@/core/layout/models/Sidebar";
import type { Theme } from "@/core/theme/models/AppTheme";
import type { Dispatch, SetStateAction } from "react";

export interface IMobileNavigationThemeMenuProperties {
	theme: Theme;
	menuItemStyle: MenuItemStyles;
	setDrawerOpen: Dispatch<SetStateAction<boolean>>;
	width: number;
}

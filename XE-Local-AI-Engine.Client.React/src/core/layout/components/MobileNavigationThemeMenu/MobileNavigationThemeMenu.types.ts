import type { MenuItemStyles } from "@/core/layout/models/Sidebar";
import type { Dispatch, SetStateAction } from "react";

export interface IMobileNavigationThemeMenuProperties {
	menuItemStyle: MenuItemStyles;
	setDrawerOpen: Dispatch<SetStateAction<boolean>>;
	/** Reports a sub-panel opening or closing to the parent drawer. */
	onPanelOpenChange: (open: boolean) => void;
	width: number;
}

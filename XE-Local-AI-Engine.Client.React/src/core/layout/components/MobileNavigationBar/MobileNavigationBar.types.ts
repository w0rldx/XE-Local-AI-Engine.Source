import type { Dispatch, SetStateAction } from "react";

export interface IMobileNavigationBarProperties {
	drawerOpen: boolean;
	setDrawerOpen: Dispatch<SetStateAction<boolean>>;
}

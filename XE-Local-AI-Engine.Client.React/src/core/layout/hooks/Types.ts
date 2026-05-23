import type { Dispatch, RefObject, SetStateAction } from "react";

export interface UseMobileNavigationDrawerResult {
	isDrawerOpen: boolean;
	setIsDrawerOpen: Dispatch<SetStateAction<boolean>>;
	drawerReference: RefObject<HTMLDivElement | null>;
	menuReference: RefObject<HTMLDivElement | null>;
	openDrawer: () => void;
	closeDrawer: () => void;
}

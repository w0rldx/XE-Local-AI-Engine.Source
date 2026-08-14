import type { PropsWithChildren, RefObject } from "react";

export interface MobileNavigationDrawerPanelProperties extends PropsWithChildren {
	isOpen: boolean;
	width: number;
	title: string;
	onClose: () => void;
	drawerReference: RefObject<HTMLDivElement | null>;
}

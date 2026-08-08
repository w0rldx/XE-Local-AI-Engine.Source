import type { Theme } from "@/core/theme/models/AppTheme";
import type { PropsWithChildren, RefObject } from "react";

export interface MobileNavigationDrawerPanelProperties extends PropsWithChildren {
	isOpen: boolean;
	theme: Theme;
	width: number;
	title: string;
	onClose: () => void;
	drawerReference: RefObject<HTMLDivElement | null>;
}

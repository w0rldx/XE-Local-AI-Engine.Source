import type { PropsWithChildren } from "react";

export interface MobileNavigationDrawerPanelProperties extends PropsWithChildren {
	isOpen: boolean;
	width: number;
	title: string;
	onClose: () => void;
	/** Told when the panel's content mounts (true) and unmounts (false), whatever closed or removed it. */
	onPanelOpenChange: (open: boolean) => void;
}

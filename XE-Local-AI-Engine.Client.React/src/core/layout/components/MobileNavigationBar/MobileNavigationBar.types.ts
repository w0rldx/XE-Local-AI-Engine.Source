import type { Dispatch, SetStateAction } from "react";

/** The drawer's "Report a problem" and "About" entries; the app shell owns both flows. */
export interface IMobileNavigationDrawerActions {
	reportProblem: () => Promise<void>;
	reportProblemPending: boolean;
	openAbout: () => void;
}

export interface IMobileNavigationBarProperties {
	drawerOpen: boolean;
	setDrawerOpen: Dispatch<SetStateAction<boolean>>;
	drawerActions: IMobileNavigationDrawerActions;
}

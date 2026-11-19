export interface UseMobileNavigationDrawerResult {
	isDrawerOpen: boolean;
	openDrawer: () => void;
	/** Closes the sub-panel only, back to the parent drawer. */
	closePanel: () => void;
	/** Closes the sub-panel and the parent drawer. */
	closeDrawer: () => void;
}

export interface IDesktopNavigationBarStoreProperties {
	sidebarState: boolean;
	// Explicit open/closed state per navigation group id. A group with no entry here falls back to the
	// active-route-aware default resolved in the nav bar. Persisted so the user's expand/collapse choices
	// survive a page reload.
	openGroups: Record<string, boolean>;
	actions: {
		setSidebarState: (state: boolean) => void;
		setGroupOpen: (groupId: string, open: boolean) => void;
	};
}

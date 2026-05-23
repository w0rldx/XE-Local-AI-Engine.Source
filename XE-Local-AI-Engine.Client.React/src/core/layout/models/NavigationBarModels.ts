export interface IDesktopNavigationBarStoreProperties {
	sidebarState: boolean;
	actions: {
		setSidebarState: (state: boolean) => void;
	};
}

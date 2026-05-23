export interface SidebarState {
	collapsed: boolean;
	width: number | string;
	collapsedWidth: number | string;
	isAnimating: boolean;
	transitionDuration: number;
	syncSidebarConfig: (config: {
		collapsed?: boolean;
		width?: number | string;
		collapsedWidth?: number | string;
		transitionDuration?: number;
	}) => void;
	startSidebarAnimation: () => void;
	finishSidebarAnimation: () => void;
}

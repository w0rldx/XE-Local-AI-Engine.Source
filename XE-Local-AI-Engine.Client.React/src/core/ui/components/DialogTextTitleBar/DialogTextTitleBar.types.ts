export interface IDialogTextTitleBarProperties {
	title: string;
	handleClose: () => void;
	showCloseButton?: boolean;
	/** Whether the fullscreen toggle ActionIcon is shown. Defaults to false. */
	showFullScreenToggle?: boolean;
	/** Current fullscreen state, used to pick the icon and tooltip. */
	isFullScreen?: boolean;
	/** Invoked when the fullscreen toggle is clicked. */
	onToggleFullScreen?: () => void;
}

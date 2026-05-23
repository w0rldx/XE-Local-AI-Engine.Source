import { Modal } from "@mantine/core";
import type { ModalProps } from "@mantine/core";
import type { ReactNode } from "react";

import { DialogTextTitleBar } from "@/core/ui/components/DialogTextTitleBar/DialogTextTitleBar";

export interface IDialogShellProps extends Omit<ModalProps, "title" | "withCloseButton"> {
	/** Dialog title displayed via the shared DialogTextTitleBar. */
	title: string;
	/** Whether to show the close button in the title bar. Defaults to true. */
	showCloseButton?: boolean;
	/** Modal content. */
	children: ReactNode;
}

/**
 * Normalized dialog shell that wraps Mantine Modal with the application's
 * consistent overlay, transition, title bar, and sizing defaults.
 *
 * All dialogs across the app (core and chat) should use this component
 * to ensure uniform behavior and appearance.
 */
export function DialogShell({
	title,
	showCloseButton = true,
	children,
	onClose,
	...modalProps
}: IDialogShellProps) {
	return (
		<Modal
			centered={true}
			withCloseButton={false}
			overlayProps={{
				backgroundOpacity: 0.55,
				blur: 3,
				...modalProps.overlayProps,
			}}
			onClose={onClose}
			{...modalProps}
		>
			<DialogTextTitleBar title={title} handleClose={onClose} showCloseButton={showCloseButton} />
			{children}
		</Modal>
	);
}
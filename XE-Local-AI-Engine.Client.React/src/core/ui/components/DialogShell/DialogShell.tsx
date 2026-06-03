import type { ModalProps } from "@mantine/core";
import { Group, Modal, ScrollArea } from "@mantine/core";
import { type ReactNode, use, useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import { DialogTextTitleBar } from "@/core/ui/components/DialogTextTitleBar/DialogTextTitleBar";
import { ConfirmContext } from "@/core/ui/context/ConfirmContext";

export interface IDialogShellProps extends Omit<ModalProps, "title" | "withCloseButton"> {
	/** Dialog title displayed via the shared DialogTextTitleBar. */
	title: string;
	/** Whether to show the close button in the title bar. Defaults to true. */
	showCloseButton?: boolean;
	/** Whether the title-bar fullscreen toggle is available. Defaults to true. */
	enableFullScreenToggle?: boolean;
	/**
	 * When true, the dialog cannot be dismissed by clicking the overlay or pressing Escape, and
	 * the title-bar close button routes through a confirmation dialog before invoking onClose.
	 * Use for editors with unsaved changes or in-flight operations. Defaults to false.
	 */
	confirmCloseWhen?: boolean;
	/** Optional sticky footer (e.g. Save/Cancel actions) pinned to the bottom of the scroll area. */
	footer?: ReactNode;
	/** Modal content. */
	children: ReactNode;
}

// Body padding token: the sticky footer's negative margins cancel Modal.Body's padding so the
// footer's border and background bleed to the dialog edges. Falls back to Mantine's modal padding.
const MODAL_PADDING = "var(--mb-padding, var(--mantine-spacing-md))";

/**
 * Normalized dialog shell that wraps Mantine Modal with the application's consistent overlay,
 * transition, title bar, and sizing defaults.
 *
 * Supports a title-bar fullscreen toggle (on by default), a sticky footer slot, an autosizing
 * scroll body, and an opt-in close confirmation for editors with unsaved/in-flight state.
 *
 * All dialogs across the app (core and chat) should use this component to ensure uniform behavior.
 */
export function DialogShell({
	title,
	showCloseButton = true,
	enableFullScreenToggle = true,
	confirmCloseWhen = false,
	footer,
	children,
	onClose,
	// Slightly wider default than Mantine's "xl"; capped at 95vw so it never exceeds the viewport on small screens.
	size = "min(54rem, 95vw)",
	scrollAreaComponent = ScrollArea.Autosize,
	closeOnClickOutside,
	closeOnEscape,
	transitionProps,
	radius,
	onExitTransitionEnd,
	...modalProps
}: IDialogShellProps) {
	const { t } = useTranslation();
	// Tolerant access: DialogShell may render outside a ConfirmProvider, so consume the context
	// directly rather than via the throwing useConfirm() hook. Only used when confirmCloseWhen.
	const confirmContext = use(ConfirmContext);
	const [isFullScreen, setIsFullScreen] = useState(false);

	const toggleFullScreen = useCallback(() => {
		setIsFullScreen((previous) => !previous);
	}, []);

	const handleClose = useCallback(async () => {
		if (!confirmCloseWhen) {
			onClose?.();
			return;
		}

		// Without a ConfirmProvider we cannot prompt; fall back to a direct close so the dialog
		// never becomes undismissable.
		if (!confirmContext) {
			onClose?.();
			return;
		}

		const confirmed = await confirmContext.confirm({
			title: t("components.dialogShell.unsavedTitle"),
			description: t("components.dialogShell.unsavedDescription"),
			confirmationText: t("common.discard"),
			cancellationText: t("common.keepEditing"),
		});
		if (confirmed) {
			onClose?.();
		}
	}, [confirmCloseWhen, confirmContext, onClose, t]);

	// Reset fullscreen once the dialog has finished closing so it reopens in its default state.
	const handleExitTransitionEnd = useCallback(() => {
		setIsFullScreen(false);
		onExitTransitionEnd?.();
	}, [onExitTransitionEnd]);

	return (
		<Modal
			centered={true}
			withCloseButton={false}
			fullScreen={isFullScreen}
			size={size}
			radius={isFullScreen ? 0 : radius}
			scrollAreaComponent={scrollAreaComponent}
			transitionProps={isFullScreen ? { transition: "fade" } : transitionProps}
			closeOnClickOutside={confirmCloseWhen ? false : closeOnClickOutside}
			closeOnEscape={confirmCloseWhen ? false : closeOnEscape}
			overlayProps={{
				backgroundOpacity: 0.55,
				blur: 3,
				...modalProps.overlayProps,
			}}
			onClose={handleClose}
			onExitTransitionEnd={handleExitTransitionEnd}
			{...modalProps}
		>
			<DialogTextTitleBar
				title={title}
				handleClose={handleClose}
				showCloseButton={showCloseButton}
				showFullScreenToggle={enableFullScreenToggle}
				isFullScreen={isFullScreen}
				onToggleFullScreen={toggleFullScreen}
			/>
			{children}
			{footer ? (
				<Group
					justify="flex-end"
					pos="sticky"
					bottom={0}
					bg="var(--mantine-color-body)"
					px="md"
					py="md"
					mt="xs"
					mx={`calc(${MODAL_PADDING} * -1)`}
					mb={`calc(${MODAL_PADDING} * -1)`}
					style={{ borderTop: "1px solid var(--mantine-color-default-border)", zIndex: 2 }}
				>
					{footer}
				</Group>
			) : null}
		</Modal>
	);
}

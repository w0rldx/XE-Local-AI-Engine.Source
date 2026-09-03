import type { ModalProps } from "@mantine/core";
import { Group, Modal, ScrollArea } from "@mantine/core";
import { type ReactNode, use, useCallback, useState } from "react";
import { useTranslation } from "react-i18next";

import { DESKTOP_NAV_BREAKPOINT } from "@/core/layout/constants/LayoutBreakpoints";
import useWindowDimensions from "@/core/layout/hooks/useWindowDimensions";
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
	/**
	 * Test id for the dialog. Declared explicitly (rather than left to Mantine's prop spread) because it is routed to
	 * the Modal's `content` section instead of its root — see the destructuring comment below for why that matters.
	 */
	"data-testid"?: string;
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
	// A test id given to a dialog must identify the VISIBLE dialog, not Mantine's portal wrapper. Mantine spreads any
	// unrecognized prop onto the Modal ROOT, which is a zero-size portal container: Playwright resolves the element but
	// reports it `hidden`, so `waitFor({ state: "visible" })` times out against a dialog that is plainly on screen
	// (live-observed on `onboarding-welcome-dialog`). Mantine 9's Styles API `attributes` prop lets us put the id on the
	// `content` section — the actual dialog card — instead, with no DOM changes. Intercepted here so every DialogShell
	// call site is correct by construction rather than each having to remember.
	"data-testid": testId,
	attributes,
	...modalProps
}: IDialogShellProps) {
	const { t } = useTranslation();
	// Tolerant access: DialogShell may render outside a ConfirmProvider, so consume the context
	// directly rather than via the throwing useConfirm() hook. Only used when confirmCloseWhen.
	const confirmContext = use(ConfirmContext);
	const [isFullScreen, setIsFullScreen] = useState(false);
	// Below the app shell's desktop-navigation cutoff (the same DESKTOP_NAV_BREAKPOINT Layout.tsx branches on)
	// dialogs go full-screen automatically: a floating card on a phone wastes gutter space and traps nested scroll
	// areas. The user-facing toggle is hidden in that state since leaving full-screen isn't meaningful there.
	const { width } = useWindowDimensions();
	const isMobileViewport = width < DESKTOP_NAV_BREAKPOINT;
	const effectiveFullScreen = isFullScreen || isMobileViewport;

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
			fullScreen={effectiveFullScreen}
			size={size}
			radius={effectiveFullScreen ? 0 : radius}
			scrollAreaComponent={scrollAreaComponent}
			transitionProps={effectiveFullScreen ? { transition: "fade" } : transitionProps}
			closeOnClickOutside={confirmCloseWhen ? false : closeOnClickOutside}
			closeOnEscape={confirmCloseWhen ? false : closeOnEscape}
			overlayProps={{
				backgroundOpacity: 0.55,
				blur: 3,
				...modalProps.overlayProps,
			}}
			onClose={handleClose}
			onExitTransitionEnd={handleExitTransitionEnd}
			attributes={testId ? { ...attributes, content: { "data-testid": testId, ...attributes?.content } } : attributes}
			{...modalProps}
		>
			<DialogTextTitleBar
				title={title}
				handleClose={handleClose}
				showCloseButton={showCloseButton}
				showFullScreenToggle={enableFullScreenToggle && !isMobileViewport}
				isFullScreen={effectiveFullScreen}
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

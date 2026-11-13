import { ActionIcon, Group, Modal, Tooltip } from "@mantine/core";
import { IconMaximize, IconMinimize, IconX } from "@tabler/icons-react";
import { useTranslation } from "react-i18next";

import type { IDialogTextTitleBarProperties } from "@/core/ui/components/DialogTextTitleBar/DialogTextTitleBar.types";

export function DialogTextTitleBar({
	title,
	handleClose,
	showCloseButton = true,
	showFullScreenToggle = false,
	isFullScreen = false,
	onToggleFullScreen,
}: IDialogTextTitleBarProperties) {
	const { t } = useTranslation();

	const fullScreenLabel = isFullScreen ? t("components.dialogShell.exitFullscreen") : t("components.dialogShell.fullscreen");
	const closeLabel = t("components.dialogTextTitleBar.tooltip.close", "Close");

	// Title bar aligns to Modal.Body's own padding (no extra horizontal padding of its own, which would inset the
	// header further than the dialog content on both sides). One Group with align="center" keeps the
	// title and the action icons on the same horizontal line; both icons are rendered identically (no wrapper
	// div) so the fullscreen toggle and close button line up with each other and with the title.
	return (
		<Group justify="space-between" align="center" wrap="nowrap" gap="sm" mb="md">
			{/* minWidth 0 + an anywhere-break: an unspaced title (a file name) wraps instead of widening the dialog past a
			    phone's edge and pushing the action icons off-screen. Modal.Title, not Text: Mantine names the dialog
			    (aria-labelledby) only while its own title is mounted, so a plain Text left every dialog unnamed. */}
			<Modal.Title fz="xl" lh="xl" style={{ minWidth: 0, overflowWrap: "anywhere" }}>
				{title}
			</Modal.Title>
			<Group gap="xs" align="center" wrap="nowrap">
				{showFullScreenToggle ? (
					<Tooltip label={fullScreenLabel}>
						<ActionIcon aria-label={fullScreenLabel} onClick={onToggleFullScreen} variant="subtle" color="gray">
							{isFullScreen ? <IconMinimize /> : <IconMaximize />}
						</ActionIcon>
					</Tooltip>
				) : null}
				{showCloseButton ? (
					<Tooltip label={closeLabel}>
						<ActionIcon aria-label={closeLabel} onClick={handleClose} variant="subtle" color="gray">
							<IconX />
						</ActionIcon>
					</Tooltip>
				) : null}
			</Group>
		</Group>
	);
}

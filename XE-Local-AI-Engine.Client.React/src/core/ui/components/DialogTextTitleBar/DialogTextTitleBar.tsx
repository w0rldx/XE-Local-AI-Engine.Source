import { ActionIcon, Group, Text, Tooltip } from "@mantine/core";
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

	// Title bar aligns to Modal.Body's own padding (no extra horizontal padding of its own, which previously
	// inset the header further than the dialog content on both sides). One Group with align="center" keeps the
	// title and the action icons on the same horizontal line; both icons are rendered identically (no wrapper
	// div) so the fullscreen toggle and close button line up with each other and with the title.
	return (
		<Group justify="space-between" align="center" wrap="nowrap" gap="sm" mb="md">
			<Text size="xl">{title}</Text>
			<Group gap="xs" align="center" wrap="nowrap">
				{showFullScreenToggle ? (
					<Tooltip label={fullScreenLabel}>
						<ActionIcon aria-label={fullScreenLabel} onClick={onToggleFullScreen} variant="subtle" color="gray">
							{isFullScreen ? <IconMinimize /> : <IconMaximize />}
						</ActionIcon>
					</Tooltip>
				) : null}
				{showCloseButton ? (
					<Tooltip label={t("components.dialogTextTitleBar.tooltip.close")}>
						<ActionIcon aria-label="close" onClick={handleClose} variant="subtle" color="gray">
							<IconX />
						</ActionIcon>
					</Tooltip>
				) : null}
			</Group>
		</Group>
	);
}

import { ActionIcon, Divider, Drawer, Title } from "@mantine/core";
import { IconX } from "@tabler/icons-react";
import { useCallback } from "react";
import { useTranslation } from "react-i18next";
import type { MobileNavigationDrawerPanelProperties } from "@/core/layout/components/MobileNavigationDrawerPanel/MobileNavigationDrawerPanel.types";

// A sub-panel over the mobile navigation drawer. Mantine's Drawer gives it the dialog role, Escape and overlay-click
// close, a focus trap, and focus back on the item that opened it.
export function MobileNavigationDrawerPanel({
	isOpen,
	width,
	title,
	onClose,
	onPanelOpenChange,
	children,
}: MobileNavigationDrawerPanelProperties) {
	const { t } = useTranslation();
	// The parent drawer stops closing on Escape while a panel is open, because both Drawers listen on the window. Reported
	// from the content's mount and unmount, so it also hears "closed" when the panel goes away without being closed: its
	// menu leaving the drawer, or the drawer itself closing.
	const reportPresence = useCallback(
		(node: HTMLDivElement | null) => {
			if (node === null) {
				return;
			}
			onPanelOpenChange(true);
			return () => onPanelOpenChange(false);
		},
		[onPanelOpenChange],
	);

	return (
		<Drawer
			opened={isOpen}
			onClose={onClose}
			position="left"
			padding={0}
			withCloseButton={false}
			overlayProps={{ backgroundOpacity: 0.5, blur: 0 }}
			// The content section is the role="dialog" element; a prop on the Drawer itself would land on its portal root.
			attributes={{ content: { "aria-label": title } }}
			styles={{ content: { width: width < 420 ? "100%" : "min(400px, 100vw)" } }}
		>
			<div ref={reportPresence} className="pt-3 w-full">
				<div className="flex flex-row justify-between items-center pb-4 px-2">
					<Title order={4} className="px-2 py-1 flex flex-row justify-between items-center">
						{title}
					</Title>

					<ActionIcon onClick={onClose} variant="subtle" aria-label={t("common.close")}>
						<IconX size={24} />
					</ActionIcon>
				</div>
				<Divider />
				{children}
			</div>
		</Drawer>
	);
}

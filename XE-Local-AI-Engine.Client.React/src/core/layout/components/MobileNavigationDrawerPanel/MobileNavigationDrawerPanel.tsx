import { ActionIcon, Divider, Portal, Title } from "@mantine/core";
import { IconX } from "@tabler/icons-react";
import { AnimatePresence, m } from "framer-motion";
import type { MobileNavigationDrawerPanelProperties } from "@/core/layout/components/MobileNavigationDrawerPanel/MobileNavigationDrawerPanel.types";

import "./MobileNavigationDrawerPanel.css";

export function MobileNavigationDrawerPanel({
	isOpen,
	theme,
	width,
	title,
	onClose,
	drawerReference,
	children,
}: MobileNavigationDrawerPanelProperties) {
	const panelWidth = width < 420 ? "100%" : "min(400px, 100vw)";

	return (
		<Portal>
			<AnimatePresence>
				{isOpen && (
					<>
						{/* Scrim — closes the sub-panel when tapped */}
						<m.div
							key="scrim"
							className="mobile-navigation-drawer-scrim"
							initial={{ opacity: 0 }}
							animate={{ opacity: 1 }}
							exit={{ opacity: 0 }}
							transition={{ duration: 0.2 }}
							onClick={onClose}
						/>

						{/* Sub-panel — portalled to body so position:fixed is viewport-relative */}
						<m.div
							key="panel"
							ref={drawerReference}
							initial={{ opacity: 0, x: -20 }}
							animate={{ opacity: 1, x: 0 }}
							exit={{ opacity: 0, x: -20 }}
							transition={{ duration: 0.2 }}
							className="mobile-navigation-drawer-panel h-full"
							style={{
								backgroundColor: theme.palette.background.default,
								borderRight: `1px solid ${theme.palette.divider}`,
								width: panelWidth,
							}}
						>
							<div className="pt-3 w-full">
								<div className="flex flex-row justify-between items-center pb-4 px-2">
									<Title order={4} className="px-2 py-1 flex flex-row justify-between items-center">
										{title}
									</Title>

									<ActionIcon onClick={onClose} variant="subtle">
										<IconX size={24} />
									</ActionIcon>
								</div>
								<Divider />
								{children}
							</div>
						</m.div>
					</>
				)}
			</AnimatePresence>
		</Portal>
	);
}

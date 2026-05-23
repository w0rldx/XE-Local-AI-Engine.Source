import { ActionIcon, Divider, Title } from "@mantine/core";
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
	return (
		<AnimatePresence>
			{isOpen && (
				<m.div
					ref={drawerReference}
					initial={{ opacity: 0, x: -20 }}
					animate={{ opacity: 1, x: 0 }}
					exit={{ opacity: 0, x: -20 }}
					transition={{ duration: 0.2 }}
					className="mobile-navigation-drawer-panel h-full"
					style={{
						backgroundColor: theme.palette.background.default,
						borderRight: `1px solid ${theme.palette.divider}`,
						zIndex: 30,
						minWidth: width >= 420 ? "400px" : "100%",
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
			)}
		</AnimatePresence>
	);
}

import { Divider, List, Text } from "@mantine/core";
import { useNavigate } from "@tanstack/react-router";
import type { RefObject } from "react";
import { useCallback } from "react";

import { MobileNavigationDrawerPanel } from "@/core/layout/components/MobileNavigationDrawerPanel/MobileNavigationDrawerPanel";
import type {
	IMobileNavigationMenuLink,
	IMobileNavigationMenuProperties,
} from "@/core/layout/components/MobileNavigationMenu/MobileNavigationMenu.types";
import { SidebarMenu } from "@/core/layout/components/Sidebar/SidebarMenu";
import { SidebarMenuItem } from "@/core/layout/components/Sidebar/SidebarMenuItem";
import { useMobileNavigationDrawer } from "@/core/layout/hooks/useMobileNavigationDrawer";

interface MobileNavigationDrawerLinksProperties {
	links: IMobileNavigationMenuLink[];
	onLinkClick: (link: IMobileNavigationMenuLink) => void | Promise<void>;
}

function MobileNavigationDrawerLinks({ links, onLinkClick }: MobileNavigationDrawerLinksProperties) {
	return links.map((link) => (
		<div key={`item-${link.to ?? link.label}`}>
			<div className="h-17 flex items-center">
				<SidebarMenuItem icon={link.icon} onClick={() => onLinkClick(link)} active={link.active} isMobile={true}>
					<Text size="sm" fw={500} lh="1.5">
						{link.label}
					</Text>
				</SidebarMenuItem>
			</div>
			<Divider />
		</div>
	));
}

interface MobileNavigationRootItemProperties {
	menuItem: IMobileNavigationMenuProperties["menuItem"];
	menuReference: RefObject<HTMLDivElement | null>;
	onOpen: () => void;
}

function MobileNavigationRootItem({ menuItem, menuReference, onOpen }: MobileNavigationRootItemProperties) {
	return (
		<div ref={menuReference} className="h-17 flex items-center justify-center">
			<SidebarMenuItem icon={menuItem.icon} onClick={onOpen} active={menuItem.active} isMobile={true}>
				<Text size="sm" fw={500} lh="1.5">
					{menuItem.label}
				</Text>
			</SidebarMenuItem>
		</div>
	);
}

export function MobileNavigationMenu({
	theme,
	menuItemStyle,
	setDrawerOpen,
	menuItem,
	drawerTitle,
	links,
	shouldRender,
	width,
}: IMobileNavigationMenuProperties) {
	const { isDrawerOpen, drawerReference, menuReference, openDrawer, closeDrawer, setIsDrawerOpen } =
		useMobileNavigationDrawer(setDrawerOpen);
	const navigate = useNavigate();

	const handleDrawerClose = useCallback(() => {
		closeDrawer();
	}, [closeDrawer]);

	const handleDrawerOpen = () => {
		if (links && links.length > 0) {
			openDrawer();
		} else if (menuItem.onClick) {
			menuItem.onClick();
			handleDrawerClose();
		}
	};

	const handleLinkClick = async (link: IMobileNavigationMenuLink) => {
		if (link.onClick) {
			link.onClick();
		} else if (link.to) {
			await navigate({ to: link.to });
		}
		handleDrawerClose();
	};

	const drawerContent = (
		<MobileNavigationDrawerPanel
			isOpen={isDrawerOpen}
			theme={theme}
			width={width}
			title={drawerTitle ?? ""}
			onClose={() => setIsDrawerOpen(false)}
			drawerReference={drawerReference}
		>
			<List className="gap-2 flex flex-col">
				{links ? <MobileNavigationDrawerLinks links={links} onLinkClick={handleLinkClick} /> : null}
			</List>
		</MobileNavigationDrawerPanel>
	);

	if (shouldRender === false) {
		return null;
	}

	return (
		<SidebarMenu menuItemStyles={menuItemStyle}>
			<MobileNavigationRootItem menuItem={menuItem} menuReference={menuReference} onOpen={handleDrawerOpen} />
			{drawerContent}
		</SidebarMenu>
	);
}

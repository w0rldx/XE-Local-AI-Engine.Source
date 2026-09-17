import { Divider, List, Text } from "@mantine/core";
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
	onLinkClick: (link: IMobileNavigationMenuLink) => void;
}

function MobileNavigationDrawerLinks({ links, onLinkClick }: MobileNavigationDrawerLinksProperties) {
	return links.map((link) => (
		<div key={`item-${link.to ?? link.label}`}>
			<div className="h-17 flex items-center">
				{/* `to` only when the link has no onClick of its own: an onClick used to run INSTEAD of navigating,
				    and an anchor would navigate as well. */}
				<SidebarMenuItem
					icon={link.icon}
					to={link.onClick ? undefined : link.to}
					onClick={() => onLinkClick(link)}
					active={link.active}
					isMobile={true}
				>
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
	/** Route the item leads to, or undefined when it is a disclosure that opens the sub-panel instead. */
	to?: string;
	onOpen: () => void;
}

function MobileNavigationRootItem({ menuItem, menuReference, to, onOpen }: MobileNavigationRootItemProperties) {
	return (
		<div ref={menuReference} className="h-17 flex items-center justify-center">
			<SidebarMenuItem icon={menuItem.icon} to={to} onClick={onOpen} active={menuItem.active} isMobile={true}>
				<Text size="sm" fw={500} lh="1.5">
					{menuItem.label}
				</Text>
			</SidebarMenuItem>
		</div>
	);
}

export function MobileNavigationMenu({
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

	// A group's root item is a disclosure for the sub-panel, so it never becomes an anchor; a flat one leads
	// somewhere and does, unless it carries an onClick of its own (which used to run INSTEAD of navigating).
	const hasLinks = (links?.length ?? 0) > 0;
	const rootItemTo = hasLinks || menuItem.onClick ? undefined : menuItem.to;

	const handleDrawerClose = useCallback(() => {
		closeDrawer();
	}, [closeDrawer]);

	const handleDrawerOpen = () => {
		if (hasLinks) {
			openDrawer();
			return;
		}

		menuItem.onClick?.();
		// A flat item's own href does the navigating; either way the drawer it was tapped in has to go away.
		handleDrawerClose();
	};

	const handleLinkClick = (link: IMobileNavigationMenuLink) => {
		// Same split one level down: an onClick replaces navigation, otherwise the anchor's href carries it.
		link.onClick?.();
		handleDrawerClose();
	};

	const drawerContent = (
		<MobileNavigationDrawerPanel
			isOpen={isDrawerOpen}
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
			<MobileNavigationRootItem menuItem={menuItem} menuReference={menuReference} to={rootItemTo} onOpen={handleDrawerOpen} />
			{drawerContent}
		</SidebarMenu>
	);
}

// @vitest-environment jsdom

import { cleanup, fireEvent, screen, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { MobileNavigationMenu } from "@/core/layout/components/MobileNavigationMenu/MobileNavigationMenu";
import type { IMobileNavigationMenuProperties } from "@/core/layout/components/MobileNavigationMenu/MobileNavigationMenu.types";
import { SidebarMenuItem } from "@/core/layout/components/Sidebar/SidebarMenuItem";
import { renderWithProviders } from "@/test/RenderWithProviders";

// Labels here are props: the parent (MobileNavigationBar) is what calls `t()`, so this file passes plain strings.
// The one string the tree translates itself is the sub-panel's close button, so that bundle entry is what proves
// the panel opened.
const CLOSE_LABEL = "Close";

function renderMenu(overrides: Partial<IMobileNavigationMenuProperties> = {}) {
	const setDrawerOpen = vi.fn();

	const result = renderWithProviders(
		<MobileNavigationMenu
			menuItemStyle={{}}
			setDrawerOpen={setDrawerOpen}
			menuItem={{ icon: null, label: "Chat" }}
			width={390}
			{...overrides}
		/>,
		{ route: "/" },
	);

	return { ...result, setDrawerOpen };
}

describe("MobileNavigationMenu", () => {
	afterEach(() => {
		cleanup();
	});

	// The drawer used to turn every route into a navigate() closure on a <button>, which announces a destination as
	// a button and throws away middle-click, ctrl-click and "open in new tab" — the browser only offers those for an
	// href. `active` is the same matchesNavRoute result the rail computes, so it carries aria-current too.
	it("renders a flat root item with a route as the current-page link", async () => {
		renderMenu({ menuItem: { icon: null, label: "Chat", to: "/chat", active: true } });

		const chat = await screen.findByRole("link", { name: "Chat" });
		expect(chat.getAttribute("href")).toBe("/chat");
		expect(chat.getAttribute("aria-current")).toBe("page");
	});

	// A group's root item leads nowhere: it discloses the sub-panel that holds the children, so it has to stay a
	// button. Its children are destinations, so they are links.
	it("keeps a group's root item a button that opens a sub-panel of links", async () => {
		renderMenu({
			menuItem: { icon: null, label: "Settings" },
			drawerTitle: "Settings",
			links: [
				{ label: "Node Settings", to: "/node-settings" },
				{ label: "Diagnostics", to: "/diagnostics" },
			],
		});

		const root = await screen.findByRole("button", { name: "Settings" });
		expect(root.getAttribute("href")).toBeNull();
		expect(screen.queryByRole("link", { name: "Settings" })).toBeNull();

		fireEvent.click(root);

		// The panel's own close button is the translated string, so finding it proves the sub-panel really mounted.
		const panel = (await screen.findByRole("button", { name: CLOSE_LABEL })).closest("div[class*='panel']");
		expect(panel).not.toBeNull();

		const inPanel = within(panel as HTMLElement);
		expect(inPanel.getByRole("link", { name: "Node Settings" }).getAttribute("href")).toBe("/node-settings");
		expect(inPanel.getByRole("link", { name: "Diagnostics" }).getAttribute("href")).toBe("/diagnostics");
	});

	// Before the change an entry with both `to` and `onClick` ran the onClick INSTEAD of navigating. An anchor would
	// do both, so such an entry keeps no href at all — navigation in this tree now happens only through one.
	it("keeps an entry that carries its own onClick a button and runs it once", async () => {
		const onClick = vi.fn();
		renderMenu({
			menuItem: { icon: null, label: "Preview" },
			drawerTitle: "Preview",
			links: [{ label: "Images", to: "/images", onClick }],
		});

		fireEvent.click(await screen.findByRole("button", { name: "Preview" }));

		const entry = await screen.findByRole("button", { name: "Images" });
		expect(entry.tagName).toBe("BUTTON");
		expect(entry.getAttribute("href")).toBeNull();
		expect(screen.queryByRole("link", { name: "Images" })).toBeNull();

		fireEvent.click(entry);
		expect(onClick).toHaveBeenCalledTimes(1);
	});

	// The href navigates, but nothing else closes the drawer the tap happened in, so the menu still has to run the
	// close path itself — once, and for the parent drawer too (closeDrawer calls setDrawerOpen(false)).
	it("closes the drawer once when a link entry is tapped", async () => {
		const { setDrawerOpen } = renderMenu({
			menuItem: { icon: null, label: "Settings" },
			drawerTitle: "Settings",
			links: [{ label: "Node Settings", to: "/node-settings" }],
		});

		fireEvent.click(await screen.findByRole("button", { name: "Settings" }));
		expect(setDrawerOpen).not.toHaveBeenCalled();

		fireEvent.click(await screen.findByRole("link", { name: "Node Settings" }));

		expect(setDrawerOpen).toHaveBeenCalledTimes(1);
		expect(setDrawerOpen).toHaveBeenCalledWith(false);
	});

	// Tapping a flat root item never opens a sub-panel, so the same close path has to run from there too.
	it("closes the drawer once when a flat root link is tapped", async () => {
		const { setDrawerOpen } = renderMenu({ menuItem: { icon: null, label: "Chat", to: "/chat" } });

		fireEvent.click(await screen.findByRole("link", { name: "Chat" }));

		expect(setDrawerOpen).toHaveBeenCalledTimes(1);
		expect(setDrawerOpen).toHaveBeenCalledWith(false);
	});
});

describe("SidebarMenuItem", () => {
	afterEach(() => {
		cleanup();
	});

	// An anchor has no disabled state to offer — `aria-disabled` on a link still lets the browser follow the href —
	// so a disabled item drops back to the button, which can really refuse the interaction.
	it("falls back to a disabled button when a routed item is disabled", async () => {
		const onClick = vi.fn();
		renderWithProviders(
			<SidebarMenuItem to="/chat" disabled={true} onClick={onClick} isMobile={true}>
				Chat
			</SidebarMenuItem>,
			{ route: "/" },
		);

		const item = await screen.findByRole("button", { name: "Chat" });
		expect((item as HTMLButtonElement).disabled).toBe(true);
		expect(screen.queryByRole("link", { name: "Chat" })).toBeNull();

		fireEvent.click(item);
		expect(onClick).not.toHaveBeenCalled();
	});
});

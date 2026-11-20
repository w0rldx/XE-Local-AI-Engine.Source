// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { MobileNavigationMenu } from "@/core/layout/components/MobileNavigationMenu/MobileNavigationMenu";
import type { IMobileNavigationMenuProperties } from "@/core/layout/components/MobileNavigationMenu/MobileNavigationMenu.types";
import { SidebarMenuItem } from "@/core/layout/components/Sidebar/SidebarMenuItem";
import { renderWithProviders } from "@/test/RenderWithProviders";

// Labels here are props: the parent (MobileNavigationBar) is what calls `t()`, so this file passes plain strings.
function renderMenu(overrides: Partial<IMobileNavigationMenuProperties> = {}) {
	const setDrawerOpen = vi.fn();
	const onPanelOpenChange = vi.fn();

	const result = renderWithProviders(
		<MobileNavigationMenu
			menuItemStyle={{}}
			setDrawerOpen={setDrawerOpen}
			onPanelOpenChange={onPanelOpenChange}
			menuItem={{ icon: null, label: "Chat" }}
			width={390}
			{...overrides}
		/>,
		{ route: "/" },
	);

	return { ...result, setDrawerOpen, onPanelOpenChange };
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

		const inPanel = within(await screen.findByRole("dialog", { name: "Settings" }));
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

	// The router suppresses its own navigation for a modified click so the current tab stays where it is — which is
	// the whole point of the href. Closing the drawer anyway would undo half of that, so the close path must not run.
	it("leaves the drawer alone when a link entry is opened in a new tab", async () => {
		const { setDrawerOpen } = renderMenu({
			menuItem: { icon: null, label: "Settings" },
			drawerTitle: "Settings",
			links: [{ label: "Node Settings", to: "/node-settings" }],
		});

		fireEvent.click(await screen.findByRole("button", { name: "Settings" }));

		// A modified click is exactly the one the router lets through to the browser, and jsdom answers a real
		// anchor activation with a "Not implemented: navigation" error. Taking the default on the document — after
		// React's own listeners on the root container have already run — keeps the run quiet without touching the
		// handlers under test.
		const swallowNavigation = (event: MouseEvent) => event.preventDefault();
		document.addEventListener("click", swallowNavigation);

		const entry = await screen.findByRole("link", { name: "Node Settings" });
		fireEvent.click(entry, { ctrlKey: true });
		fireEvent.click(entry, { metaKey: true });

		document.removeEventListener("click", swallowNavigation);

		expect(setDrawerOpen).not.toHaveBeenCalled();
	});

	// Tapping a flat root item never opens a sub-panel, so the same close path has to run from there too.
	it("closes the drawer once when a flat root link is tapped", async () => {
		const { setDrawerOpen } = renderMenu({ menuItem: { icon: null, label: "Chat", to: "/chat" } });

		fireEvent.click(await screen.findByRole("link", { name: "Chat" }));

		expect(setDrawerOpen).toHaveBeenCalledTimes(1);
		expect(setDrawerOpen).toHaveBeenCalledWith(false);
	});
});

describe("MobileNavigationMenu sub-panel", () => {
	afterEach(() => {
		cleanup();
	});

	const groupMenu: Partial<IMobileNavigationMenuProperties> = {
		menuItem: { icon: null, label: "Settings" },
		drawerTitle: "Settings",
		links: [
			{ label: "Node Settings", to: "/node-settings" },
			{ label: "Diagnostics", to: "/diagnostics" },
		],
	};

	// The panel was a bare positioned div: Escape did nothing, focus stayed on the page behind it and a screen reader
	// was never told a dialog had opened.
	it("opens as a named dialog, moves focus in and keeps Tab inside it", async () => {
		const { onPanelOpenChange } = renderMenu(groupMenu);

		fireEvent.click(await screen.findByRole("button", { name: "Settings" }));

		const panel = await screen.findByRole("dialog", { name: "Settings" });
		expect(onPanelOpenChange).toHaveBeenLastCalledWith(true);
		await waitFor(() => expect(panel.contains(document.activeElement)).toBe(true));

		const last = within(panel).getByRole("link", { name: "Diagnostics" });
		last.focus();
		fireEvent.keyDown(last, { key: "Tab" });

		expect(panel.contains(document.activeElement)).toBe(true);
		expect(document.activeElement).not.toBe(last);
	});

	it("closes on Escape, reports it, and puts focus back on the item that opened it", async () => {
		const { onPanelOpenChange, setDrawerOpen } = renderMenu(groupMenu);
		const opener = await screen.findByRole("button", { name: "Settings" });
		opener.focus();
		fireEvent.click(opener);
		const panel = await screen.findByRole("dialog", { name: "Settings" });
		await waitFor(() => expect(panel.contains(document.activeElement)).toBe(true));

		fireEvent.keyDown(document.activeElement as Element, { key: "Escape" });

		await waitFor(() => expect(screen.queryByRole("dialog", { name: "Settings" })).toBeNull());
		await waitFor(() => expect(document.activeElement).toBe(opener));
		expect(onPanelOpenChange).toHaveBeenLastCalledWith(false);
		// Escape leaves the parent drawer to its own handling.
		expect(setDrawerOpen).not.toHaveBeenCalled();
	});
});

describe("MobileNavigationMenu sub-panel unmount", () => {
	afterEach(() => {
		cleanup();
	});

	// A group can leave the drawer while its panel is open (a capability or UI-mode change filters it out). The parent
	// stops closing on Escape while it believes a panel is open, so it must still hear that this one is gone.
	it("reports the panel closed when its menu unmounts while the panel is open", async () => {
		const { onPanelOpenChange, unmount } = renderMenu({
			menuItem: { icon: null, label: "Settings" },
			drawerTitle: "Settings",
			links: [{ label: "Node Settings", to: "/node-settings" }],
		});
		fireEvent.click(await screen.findByRole("button", { name: "Settings" }));
		await screen.findByRole("dialog", { name: "Settings" });
		expect(onPanelOpenChange).toHaveBeenLastCalledWith(true);

		unmount();

		expect(onPanelOpenChange).toHaveBeenLastCalledWith(false);
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

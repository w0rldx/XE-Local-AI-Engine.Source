// @vitest-environment jsdom

import { cleanup, fireEvent, screen, waitFor, within } from "@testing-library/react";
import { afterEach, describe, expect, it, vi } from "vitest";

import { DesktopNavigationBar } from "@/core/layout/components/DesktopNavigationBar/DesktopNavigationBar";
import { useDesktopNavigationBarStore } from "@/core/layout/stores/DesktopNavigationBarStore";
import { renderWithProviders } from "@/test/RenderWithProviders";

function renderNavigationBar(collapsed = false, route = "/") {
	return renderWithProviders(<DesktopNavigationBar sideBarCollapsed={collapsed} setSideBarCollapsed={vi.fn()} />, {
		route,
	});
}

// The rail's only group parent in every capability configuration: Settings is ungated, so this is the one
// expandable item a test can rely on being present.
const GROUP_LABEL = "Settings";

describe("DesktopNavigationBar", () => {
	afterEach(() => {
		cleanup();
		useDesktopNavigationBarStore.setState({ openGroups: {} });
	});

	// Two <nav> landmarks (this rail and the mobile drawer) are indistinguishable to a screen reader's landmark
	// list without a name, so the rail carries one from the bundle rather than being an anonymous "navigation".
	it("exposes the rail as a named navigation landmark", async () => {
		renderNavigationBar();

		expect(await screen.findByRole("navigation", { name: "Main navigation" })).toBeTruthy();
	});

	// A group parent is a disclosure button: without aria-expanded a screen reader announces a plain button and
	// never says whether its children are showing, and without aria-controls it cannot say what they are.
	it("announces a group's expanded state and points at the container it controls", async () => {
		renderNavigationBar();

		const toggle = await screen.findByRole("button", { name: GROUP_LABEL });
		expect(toggle.getAttribute("aria-expanded")).toBe("false");

		const controlledId = toggle.getAttribute("aria-controls");
		expect(controlledId).toBeTruthy();
		// A dangling reference announces nothing, so the target has to exist while the group is still closed.
		expect(document.getElementById(controlledId!)).not.toBeNull();

		fireEvent.click(toggle);
		await waitFor(() => {
			expect(toggle.getAttribute("aria-expanded")).toBe("true");
		});

		// The wiring is only meaningful if the referenced container really holds the group's children. Polled
		// rather than read straight away: Collapse reveals them across its own height transition.
		await waitFor(() => {
			const controlled = document.getElementById(controlledId!);
			expect(within(controlled!).getByRole("link", { name: "Node Settings" })).toBeTruthy();
		});

		fireEvent.click(toggle);
		await waitFor(() => {
			expect(toggle.getAttribute("aria-expanded")).toBe("false");
		});
	});

	// The collapsed rail replaces the Collapse with a Mantine Menu, which owns its own expanded state. Leaving
	// the disclosure attributes on would announce a second, always-false one that points at nothing.
	it("drops the disclosure wiring in the collapsed rail, where a menu takes over", async () => {
		renderNavigationBar(true);

		const toggle = await screen.findByRole("button", { name: GROUP_LABEL });
		expect(toggle.getAttribute("aria-controls")).toBeNull();
		expect(toggle.getAttribute("aria-expanded")).toBeNull();
	});

	// Every destination used to be an UnstyledButton calling navigate(), which announces a link as a "button" and
	// throws away middle-click, ctrl-click and "open in new tab" — the browser only offers those for a real href.
	it("renders a flat destination as a link carrying its route", async () => {
		renderNavigationBar();

		const chat = await screen.findByRole("link", { name: "Chat" });
		expect(chat.getAttribute("href")).toBe("/chat");
	});

	// Nested children are destinations too, so the same rule applies one level down.
	it("renders a group's nested destinations as links carrying their routes", async () => {
		renderNavigationBar();

		const toggle = await screen.findByRole("button", { name: GROUP_LABEL });
		fireEvent.click(toggle);

		const nodeSettings = await screen.findByRole("link", { name: "Node Settings" });
		expect(nodeSettings.getAttribute("href")).toBe("/node-settings");
	});

	// The active item keeps its aria-current from the rail's own matchesNavRoute, not from Link's active props:
	// one notion of "active" for the highlight and the announcement, seeded by the route the test rendered at.
	it("marks the link on the current route as the current page", async () => {
		renderNavigationBar();

		const home = await screen.findByRole("link", { name: "Home" });
		expect(home.getAttribute("aria-current")).toBe("page");
		expect((await screen.findByRole("link", { name: "Chat" })).getAttribute("aria-current")).toBeNull();
	});

	// TanStack's Link stamps aria-current on itself from its own prefix match, which the caller's props cannot
	// override: on /training/datasets the sibling /training entry was announced as the current page as well.
	it("marks exactly one entry as the current page when a sibling route is a prefix of it", async () => {
		renderNavigationBar(false, "/training/datasets");

		const datasets = await screen.findByRole("link", { name: "Datasets" });
		expect(datasets.getAttribute("aria-current")).toBe("page");
		const current = screen.getAllByRole("link").filter((link) => link.getAttribute("aria-current") !== null);
		expect(current.map((link) => link.getAttribute("href"))).toEqual(["/training/datasets"]);
	});

	// The collapsed rail's flyout is the only way to reach a group's children, so its entries have to be links too.
	it("renders the collapsed rail's flyout entries as links", async () => {
		renderNavigationBar(true);

		fireEvent.click(await screen.findByRole("button", { name: GROUP_LABEL }));

		const nodeSettings = await screen.findByRole("menuitem", { name: "Node Settings" });
		expect(nodeSettings.tagName).toBe("A");
		expect(nodeSettings.getAttribute("href")).toBe("/node-settings");
	});

	// The bar used to paint over the group chevrons and the active-item highlight at Mantine's 12px default.
	// These are the props that move it into the navbar's right-hand gutter; `offsetScrollbars` in particular has
	// to stay false or the app-wide "present" default would pad the items back off the edge it just freed.
	it("renders the link list with a thin vertical-only scrollbar and no content offset", async () => {
		const { container } = renderNavigationBar();
		await screen.findByRole("navigation", { name: "Main navigation" });

		// Mantine emits scrollbarSize as an inline CSS variable on the root: rem(6) === 0.375rem.
		const root = container.querySelector<HTMLElement>(".mantine-ScrollArea-root");
		expect(root).not.toBeNull();
		expect(root!.style.getPropertyValue("--scrollarea-scrollbar-size")).toContain("0.375rem");

		// scrollbars="y": the horizontal bar is not rendered at all, so it can never appear under the footer.
		expect(container.querySelector('.mantine-ScrollArea-scrollbar[data-orientation="horizontal"]')).toBeNull();
		expect(container.querySelector('.mantine-ScrollArea-scrollbar[data-orientation="vertical"]')).not.toBeNull();

		// Mantine turns offsetScrollbars into `data-offset-scrollbars` on the viewport, which is what pads the
		// content away from the bar. Absent = false = the items keep the width the CSS gutter gave them.
		const viewport = container.querySelector(".mantine-ScrollArea-viewport");
		expect(viewport!.getAttribute("data-offset-scrollbars")).toBeNull();
	});
});

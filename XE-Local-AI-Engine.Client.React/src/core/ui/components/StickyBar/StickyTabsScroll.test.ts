// @vitest-environment jsdom

import { afterEach, describe, expect, it } from "vitest";

import { scrollStuckTabsToTop } from "@/core/ui/components/StickyBar/StickyTabsScroll";

// jsdom has no layout: each element reports the viewport top it is given here.
function placeAt(element: HTMLElement, top: number): void {
	element.getBoundingClientRect = () => ({ top }) as DOMRect;
}

function mountTabsIn(scrollerTop: number, tabsTop: number): { scroller: HTMLElement; tabs: HTMLElement } {
	const scroller = document.createElement("main");
	scroller.style.overflowY = "auto";
	// The clipping wrapper stands for a Card: `clip` does not make it the scroller.
	const card = document.createElement("div");
	card.style.overflow = "clip";
	const tabs = document.createElement("div");
	card.append(tabs);
	scroller.append(card);
	document.body.append(scroller);
	placeAt(scroller, scrollerTop);
	placeAt(tabs, tabsTop);
	scroller.scrollTop = 500;
	return { scroller, tabs };
}

describe("scrollStuckTabsToTop", () => {
	afterEach(() => {
		document.body.replaceChildren();
	});

	it("scrolls a stuck tab bar's root back to the top of its scroll container", () => {
		const { scroller, tabs } = mountTabsIn(60, -240);

		scrollStuckTabsToTop(tabs);

		expect(scroller.scrollTop).toBe(200);
	});

	it("leaves the page alone while the tabs have not been scrolled past", () => {
		const { scroller, tabs } = mountTabsIn(60, 180);

		scrollStuckTabsToTop(tabs);

		expect(scroller.scrollTop).toBe(500);
	});
});

// Call from a `Tabs` onChange with the Tabs root. While the tab bar is stuck (the root's top has scrolled above its
// scroll container's top), the new panel would open mid-scroll, so the root is brought back to that top edge. A bar
// that has not been scrolled past is left where it is. Instant on purpose: no motion to respect.
export function scrollStuckTabsToTop(tabsRoot: HTMLElement | null): void {
	let scroller = tabsRoot?.parentElement ?? null;
	while (scroller !== null && !/auto|scroll/.test(getComputedStyle(scroller).overflowY)) {
		scroller = scroller.parentElement;
	}
	if (tabsRoot === null || scroller === null) {
		return;
	}

	const offset = tabsRoot.getBoundingClientRect().top - scroller.getBoundingClientRect().top;
	if (offset < 0) {
		scroller.scrollTop += offset;
	}
}

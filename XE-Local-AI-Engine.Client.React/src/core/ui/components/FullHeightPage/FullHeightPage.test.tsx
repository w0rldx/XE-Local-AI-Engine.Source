// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { FullHeightPage } from "@/core/ui/components/FullHeightPage/FullHeightPage";
import { installJsdomEnvironmentMocks, renderWithMantine } from "@/test/MantineTestRender";

describe("FullHeightPage", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	// The frame's whole promise is that an inner region scrolls and the page does not. Without the guard, a child that
	// refuses to shrink spills into the Layout's own `overflow-y-auto` container and the user gets a second, outer
	// scrollbar beside the inner one — the exact defect this was added for, and one no visual test would catch in
	// jsdom, so the contract is pinned as a style assertion. `auto` (not `hidden`) on Y so a page that has not yet
	// adopted the inner-scroll pattern degrades to scrolling in place rather than losing its tail.
	it("contains its own vertical overflow so a full-height page cannot produce an outer scrollbar", () => {
		renderWithMantine(<FullHeightPage data-testid="frame">content</FullHeightPage>);

		const frame = screen.getByTestId("frame");

		expect(frame.style.overflowY).toBe("auto");
		expect(frame.style.overflowX).toBe("hidden");
		expect(frame.style.height).toBe("100%");
		expect(frame.style.minHeight).toBe("0px");
	});

	it("forwards the tour and test hooks E2E selectors depend on", () => {
		renderWithMantine(
			<FullHeightPage data-tour="chat-overview" data-testid="frame">
				content
			</FullHeightPage>,
		);

		expect(screen.getByTestId("frame").getAttribute("data-tour")).toBe("chat-overview");
	});
});

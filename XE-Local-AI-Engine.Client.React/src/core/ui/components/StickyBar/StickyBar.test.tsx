// @vitest-environment jsdom

import { screen } from "@testing-library/react";
import { describe, expect, it } from "vitest";

import { StickyBar } from "@/core/ui/components/StickyBar/StickyBar";
import { renderWithProviders } from "@/test/RenderWithProviders";

describe("StickyBar", () => {
	// Mantine's in-flow layers reach 5 (Table): a bar at or below that would lose to a Slider thumb or a segment label by
	// DOM order and let it paint over the stuck bar.
	it("stacks above Mantine's in-flow layers", () => {
		renderWithProviders(
			<StickyBar edge="top" data-testid="bar">
				bar
			</StickyBar>,
		);

		expect(screen.getByTestId("bar").style.zIndex).toBe("10");
	});
});

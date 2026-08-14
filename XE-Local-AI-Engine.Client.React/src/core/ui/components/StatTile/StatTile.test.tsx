// @vitest-environment jsdom

import { cleanup, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

import { StatTile } from "@/core/ui/components/StatTile/StatTile";
import { installJsdomEnvironmentMocks, renderWithMantine as renderWithProviders } from "@/test/MantineTestRender";

describe("StatTile", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
		vi.clearAllMocks();
	});

	it("renders the label and the value", () => {
		renderWithProviders(<StatTile label="Generation tok/s" value="42.0 tok/s" />);

		expect(screen.getByText("Generation tok/s")).toBeTruthy();
		expect(screen.getByText("42.0 tok/s")).toBeTruthy();
	});

	it("puts the value test id on the value, not on the tile", () => {
		renderWithProviders(<StatTile label="Tests passed" value={12} valueTestId="tests-passed" />);

		expect(screen.getByTestId("tests-passed").textContent).toBe("12");
	});

	it("exposes the exact value as the headline's aria-label", () => {
		renderWithProviders(
			<StatTile variant="card" label="Total tokens" value="1.2M" exactValue="1,234,567" valueTestId="total-value" />,
		);

		expect(screen.getByTestId("total-value").getAttribute("aria-label")).toBe("1,234,567");
	});

	it("leaves the headline unlabelled when there is no separate exact value", () => {
		renderWithProviders(<StatTile label="Runs" value={7} valueTestId="runs-value" />);

		expect(screen.getByTestId("runs-value").getAttribute("aria-label")).toBeNull();
	});

	it("wraps the card variant in a tile that carries the tile test id", () => {
		renderWithProviders(
			<StatTile
				variant="card"
				label="Est. cost"
				value="$1.20"
				exactValue="$1.20"
				data-testid="usage-estimated-cost"
				valueTestId="usage-estimated-cost-value"
			/>,
		);

		const tile = screen.getByTestId("usage-estimated-cost");
		expect(within(tile).getByTestId("usage-estimated-cost-value").textContent).toBe("$1.20");
		expect(within(tile).getByText("Est. cost")).toBeTruthy();
	});

	it("renders the paper variant with both label and value inside the tile", () => {
		renderWithProviders(
			<StatTile variant="paper" label="Files changed" value={3} data-testid="files-tile" valueTestId="files-value" />,
		);

		const tile = screen.getByTestId("files-tile");
		expect(within(tile).getByText("Files changed")).toBeTruthy();
		expect(within(tile).getByTestId("files-value").textContent).toBe("3");
	});
});

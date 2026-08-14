// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { SectionCard } from "@/core/ui/components/SectionCard/SectionCard";
import { installJsdomEnvironmentMocks, renderWithMantine as renderWithProviders } from "@/test/MantineTestRender";

describe("SectionCard", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders the section title as an h3 below the page's h2", () => {
		renderWithProviders(
			<SectionCard title="Registered servers">
				<p>Body</p>
			</SectionCard>,
		);

		const heading = screen.getByRole("heading", { level: 3 });
		expect(heading.textContent).toBe("Registered servers");
	});

	it("renders no heading row at all for a chrome-less content card", () => {
		renderWithProviders(
			<SectionCard data-testid="plain-card">
				<p>Body</p>
			</SectionCard>,
		);

		expect(screen.queryByRole("heading")).toBeNull();
		expect(screen.getByText("Body")).toBeTruthy();
	});

	it("renders the actions and icon slots on the heading row", () => {
		renderWithProviders(
			<SectionCard
				title="Registered servers"
				actions={<button type="button">Register</button>}
				icon={<span data-testid="section-icon" />}
			>
				<p>Body</p>
			</SectionCard>,
		);

		expect(screen.getByRole("button", { name: "Register" })).toBeTruthy();
		expect(screen.getByTestId("section-icon")).toBeTruthy();
	});

	it("still renders a heading row for an icon-only or actions-only card", () => {
		renderWithProviders(
			<SectionCard actions={<button type="button">Refresh</button>}>
				<p>Body</p>
			</SectionCard>,
		);

		expect(screen.queryByRole("heading")).toBeNull();
		expect(screen.getByRole("button", { name: "Refresh" })).toBeTruthy();
	});

	it("forwards data-testid and data-tour to the card root", () => {
		renderWithProviders(
			<SectionCard data-testid="servers-card" data-tour="servers">
				<p>Body</p>
			</SectionCard>,
		);

		expect(screen.getByTestId("servers-card").getAttribute("data-tour")).toBe("servers");
	});
});

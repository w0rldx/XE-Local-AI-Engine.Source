// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { StatusBadge } from "@/core/ui/components/StatusBadge/StatusBadge";
import { installJsdomEnvironmentMocks, renderWithMantine as renderWithProviders } from "@/test/MantineTestRender";

// The spinner is the only structural difference between a terminal and an in-progress pill, so the test
// locates it the way the DOM exposes it: Mantine's static Loader class.
function loaderIn(element: HTMLElement): Element | null {
	return element.querySelector('[class*="Loader"]');
}

describe("StatusBadge", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders the label and forwards data-testid", () => {
		renderWithProviders(<StatusBadge label="Indexed" color="green" data-testid="knowledge-status-Indexed" />);

		expect(screen.getByTestId("knowledge-status-Indexed").textContent).toContain("Indexed");
	});

	it("forwards an explicit aria-label for callers whose label is a bare status word", () => {
		renderWithProviders(<StatusBadge label="Queued" color="yellow" aria-label="Queued" data-testid="status" />);

		expect(screen.getByTestId("status").getAttribute("aria-label")).toBe("Queued");
	});

	it("renders no spinner for a terminal status", () => {
		renderWithProviders(<StatusBadge label="Failed" color="red" data-testid="status" />);

		expect(loaderIn(screen.getByTestId("status"))).toBeNull();
	});

	it("renders an inline spinner while the status is in progress", () => {
		renderWithProviders(<StatusBadge label="Indexing" color="yellow" inProgress={true} data-testid="status" />);

		const badge = screen.getByTestId("status");
		expect(loaderIn(badge)).not.toBeNull();
		expect(badge.textContent).toContain("Indexing");
	});
});

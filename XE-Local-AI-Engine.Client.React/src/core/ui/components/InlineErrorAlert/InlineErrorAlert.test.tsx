// @vitest-environment jsdom

import { cleanup, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { InlineErrorAlert } from "@/core/ui/components/InlineErrorAlert/InlineErrorAlert";
import { installJsdomEnvironmentMocks, renderWithMantine as renderWithProviders } from "@/test/MantineTestRender";

describe("InlineErrorAlert", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("announces the failure to assistive technology", () => {
		renderWithProviders(<InlineErrorAlert message="Could not load the work items." />);

		expect(screen.getByRole("alert").textContent).toContain("Could not load the work items.");
	});

	it("renders the warning icon every error banner carries", () => {
		renderWithProviders(<InlineErrorAlert message="Could not load the rule sets." />);

		// The icon is decorative, so it has no accessible name; the tabler class is how the DOM exposes it.
		expect(screen.getByRole("alert").querySelector(".tabler-icon-alert-triangle")).not.toBeNull();
	});

	it("renders a title above the message", () => {
		renderWithProviders(<InlineErrorAlert title="Local chat unavailable" message="Could not connect." />);

		const alert = screen.getByRole("alert");
		expect(within(alert).getByText("Local chat unavailable")).toBeTruthy();
		expect(within(alert).getByText("Could not connect.")).toBeTruthy();
	});

	it("renders extra content under the message", () => {
		renderWithProviders(
			<InlineErrorAlert message="Could not load this run's events.">
				<button type="button">Retry</button>
			</InlineErrorAlert>,
		);

		const alert = screen.getByRole("alert");
		expect(within(alert).getByText("Could not load this run's events.")).toBeTruthy();
		expect(within(alert).getByRole("button", { name: "Retry" })).toBeTruthy();
	});

	it("forwards data-testid", () => {
		renderWithProviders(<InlineErrorAlert message="Save failed." data-testid="mcp-form-submit-error" />);

		expect(screen.getByTestId("mcp-form-submit-error").textContent).toContain("Save failed.");
	});

	it("forwards the Mantine variant so the two existing banner styles both survive the move", () => {
		renderWithProviders(<InlineErrorAlert message="Save failed." variant="light" data-testid="error" />);

		expect(screen.getByTestId("error").getAttribute("data-variant")).toBe("light");
	});

	it("forwards spacing props", () => {
		renderWithProviders(<InlineErrorAlert message="Save failed." mt="xs" data-testid="error" />);

		expect(screen.getByTestId("error").style.marginTop).toBe("var(--mantine-spacing-xs)");
	});
});

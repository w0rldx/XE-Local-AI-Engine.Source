// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { PageShell } from "@/core/ui/components/PageShell/PageShell";
import { installJsdomEnvironmentMocks, renderWithMantine as renderWithProviders } from "@/test/MantineTestRender";

describe("PageShell", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders its children", () => {
		renderWithProviders(
			<PageShell>
				<p>First section</p>
				<p>Second section</p>
			</PageShell>,
		);

		expect(screen.getByText("First section")).toBeTruthy();
		expect(screen.getByText("Second section")).toBeTruthy();
	});

	it("forwards data-testid to the shell root", () => {
		renderWithProviders(
			<PageShell data-testid="agents-page">
				<p>Body</p>
			</PageShell>,
		);

		const shell = screen.getByTestId("agents-page");
		expect(shell.contains(screen.getByText("Body"))).toBe(true);
	});
});

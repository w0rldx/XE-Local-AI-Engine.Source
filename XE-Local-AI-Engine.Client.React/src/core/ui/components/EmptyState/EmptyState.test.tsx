// @vitest-environment jsdom

import { cleanup, screen, within } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it } from "vitest";

import { EmptyState } from "@/core/ui/components/EmptyState/EmptyState";
import { installJsdomEnvironmentMocks, renderWithMantine as renderWithProviders } from "@/test/MantineTestRender";

describe("EmptyState", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders as a single line of dimmed text when it has neither icon nor action", () => {
		renderWithProviders(<EmptyState message="No MCP servers registered yet." data-testid="mcp-servers-empty" />);

		const empty = screen.getByTestId("mcp-servers-empty");
		expect(empty.textContent).toBe("No MCP servers registered yet.");
		// The inline form is the message element itself, not a wrapper around one.
		expect(empty.children.length).toBe(0);
	});

	it("becomes a centred figure once an icon is supplied", () => {
		renderWithProviders(
			<EmptyState
				message="No usage recorded."
				icon={<span data-testid="empty-icon" />}
				data-testid="usage-empty"
			/>,
		);

		const empty = screen.getByTestId("usage-empty");
		expect(within(empty).getByTestId("empty-icon")).toBeTruthy();
		expect(empty.textContent).toContain("No usage recorded.");
	});

	it("renders a call-to-action under the message", () => {
		renderWithProviders(
			<EmptyState
				message="No agents yet."
				action={<button type="button">Create agent</button>}
				data-testid="agents-empty"
			/>,
		);

		const empty = screen.getByTestId("agents-empty");
		expect(within(empty).getByRole("button", { name: "Create agent" })).toBeTruthy();
	});

	it("scales the message to the surrounding content when a size is given", () => {
		renderWithProviders(<EmptyState message="No tools discovered." size="xs" data-testid="tools-empty" />);

		// Mantine resolves the size token to its font-size variable on the element itself, so an empty state
		// nested under an xs heading keeps that scale instead of jumping to the default md.
		expect(screen.getByTestId("tools-empty").style.getPropertyValue("--text-fz")).toBe("var(--mantine-font-size-xs)");
	});

	it("leaves the message at the default scale when no size is given", () => {
		renderWithProviders(<EmptyState message="No servers yet." data-testid="servers-empty" />);

		expect(screen.getByTestId("servers-empty").style.getPropertyValue("--text-fz")).toBe("");
	});

	it("applies the size to the icon variant too", () => {
		renderWithProviders(
			<EmptyState message="No usage." size="sm" icon={<span data-testid="icon" />} data-testid="usage-empty" />,
		);

		const message = within(screen.getByTestId("usage-empty")).getByText("No usage.");
		expect(message.style.getPropertyValue("--text-fz")).toBe("var(--mantine-font-size-sm)");
	});

	it("renders without a test id", () => {
		renderWithProviders(<EmptyState message="Nothing here." />);

		expect(screen.getByText("Nothing here.")).toBeTruthy();
	});
});

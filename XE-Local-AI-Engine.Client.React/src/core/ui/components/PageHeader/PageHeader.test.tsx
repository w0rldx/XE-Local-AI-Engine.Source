// @vitest-environment jsdom

import { cleanup, screen } from "@testing-library/react";
import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";

vi.mock("react-i18next", () => ({
	useTranslation: () => ({
		t: (key: string, defaultValue?: string) => defaultValue ?? key,
	}),
}));

import { PageHeader } from "@/core/ui/components/PageHeader/PageHeader";
import { installJsdomEnvironmentMocks, renderWithMantine as renderWithProviders } from "@/test/MantineTestRender";

describe("PageHeader", () => {
	beforeEach(() => {
		installJsdomEnvironmentMocks();
	});

	afterEach(() => {
		cleanup();
	});

	it("renders the title as the page's h2", () => {
		renderWithProviders(<PageHeader title="Agents" />);

		const heading = screen.getByRole("heading", { level: 2 });
		expect(heading.textContent).toBe("Agents");
	});

	it("falls back to the shared Worker Node eyebrow", () => {
		renderWithProviders(<PageHeader title="Agents" />);

		expect(screen.getByText("Worker Node")).toBeTruthy();
	});

	it("uses a custom eyebrow instead of the default when one is given", () => {
		renderWithProviders(<PageHeader title="Agents" eyebrow="Diagnostics" />);

		expect(screen.getByText("Diagnostics")).toBeTruthy();
		expect(screen.queryByText("Worker Node")).toBeNull();
	});

	it("renders the subtitle only when one is given", () => {
		const { unmount } = renderWithProviders(<PageHeader title="Agents" />);
		expect(screen.queryByText("Define and run agents")).toBeNull();
		unmount();

		renderWithProviders(<PageHeader title="Agents" subtitle="Define and run agents" />);
		expect(screen.getByText("Define and run agents")).toBeTruthy();
	});

	it("renders header actions", () => {
		renderWithProviders(<PageHeader title="Agents" actions={<button type="button">New agent</button>} />);

		expect(screen.getByRole("button", { name: "New agent" })).toBeTruthy();
	});

	it("forwards data-testid and data-tour to the header root", () => {
		renderWithProviders(<PageHeader title="Agents" data-testid="agents-header" data-tour="agents" />);

		const header = screen.getByTestId("agents-header");
		expect(header.getAttribute("data-tour")).toBe("agents");
	});
});
